using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PaddleOcrNet.Pdf.Internal;

/// <summary>Owns a PDFium document and the pinned input that PDFium retains until close.</summary>
internal sealed class PdfDocument : SafeHandleZeroOrMinusOneIsInvalid
{
    private GCHandle _input;
    private readonly double _scale;

    private PdfDocument(nint document, GCHandle input, double scale) : base(true)
    {
        SetHandle(document);
        _input = input;
        _scale = scale;
    }

    internal static PdfDocument Open(byte[] bytes, string? password = null, double scale = 1.0)
    {
        lock (Pdfium.SyncRoot)
        {
            var input = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                nint document = Pdfium.FPDF_LoadMemDocument(input.AddrOfPinnedObject(), bytes.Length, password ?? string.Empty);
                if (document == 0)
                {
                    ulong error = OperatingSystem.IsWindows() ? Pdfium.GetLastErrorWindows() : Pdfium.GetLastErrorUnix();
                    string message = error == 4
                        ? string.IsNullOrEmpty(password)
                            ? "The PDF is encrypted and requires a password. Set PdfOcrOptions.Password to open it."
                            : "The PDF is encrypted and the supplied PdfOcrOptions.Password is incorrect. Check the password and try again."
                        : "The PDF could not be opened. It may be corrupt, not a PDF, or password-protected/encrypted.";
                    throw new PdfProcessingException(message);
                }
                return new PdfDocument(document, input, scale);
            }
            catch
            {
                input.Free();
                throw;
            }
        }
    }

    internal int GetPageCount()
    {
        lock (Pdfium.SyncRoot)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            int count = Pdfium.FPDF_GetPageCount(handle);
            if (count <= 0) throw new InvalidOperationException("The PDF has no readable pages.");
            return count;
        }
    }

    internal PdfPage GetPageReader(int index)
    {
        lock (Pdfium.SyncRoot)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            return new PdfPage(this, index, _scale);
        }
    }

    protected override bool ReleaseHandle()
    {
        lock (Pdfium.SyncRoot)
        {
            Pdfium.FPDF_CloseDocument(handle);
            _input.Free();
            return true;
        }
    }
}

/// <summary>A page keeps its document alive; all native calls, including close, share one gate.</summary>
internal sealed class PdfPage : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly PdfDocument _document;
    private readonly double _scale;
    private bool _ownsDocument;

    internal PdfPage(PdfDocument document, int index, double scale) : base(true)
    {
        _document = document;
        _scale = scale;
        document.DangerousAddRef(ref _ownsDocument);
        try
        {
            SetHandle(Pdfium.FPDF_LoadPage(document.DangerousGetHandle(), index));
            if (IsInvalid) throw new InvalidOperationException($"PDF page {index + 1} could not be loaded.");
        }
        catch
        {
            document.DangerousRelease();
            _ownsDocument = false;
            throw;
        }
    }

    internal int GetPageWidth()
    {
        lock (Pdfium.SyncRoot)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            return checked((int)(Pdfium.FPDF_GetPageWidth(handle) * _scale));
        }
    }

    internal int GetPageHeight()
    {
        lock (Pdfium.SyncRoot)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            return checked((int)(Pdfium.FPDF_GetPageHeight(handle) * _scale));
        }
    }

    internal unsafe string GetText()
    {
        lock (Pdfium.SyncRoot)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            using var text = LoadText();
            int count = Pdfium.FPDFText_CountChars(text.DangerousGetHandle());
            if (count < 0) throw new InvalidOperationException("The PDF text could not be read.");
            var buffer = new char[checked(count + 1)];
            fixed (char* pointer = buffer)
            {
                int written = Pdfium.FPDFText_GetText(text.DangerousGetHandle(), 0, count, pointer);
                return written > 0 ? new string(pointer, 0, written - 1) : string.Empty;
            }
        }
    }

    internal IReadOnlyList<PdfTextChar> GetCharacters()
    {
        lock (Pdfium.SyncRoot)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            using var text = LoadText();
            nint textHandle = text.DangerousGetHandle();
            int count = Pdfium.FPDFText_CountChars(textHandle);
            if (count < 0) throw new InvalidOperationException("The PDF characters could not be read.");
            var result = new List<PdfTextChar>(count);
            int width = GetPageWidth(), height = GetPageHeight();
            for (int i = 0; i < count; i++)
            {
                if (Pdfium.FPDFText_GetCharBox(textHandle, i, out double left, out double right, out double bottom, out double top) == 0)
                    continue;
                // Use PDFium's device transform, including page rotation/crop, as the former reader did.
                var (x1, y1) = ToDevice(left, top, width, height);
                var (x2, y2) = ToDevice(right, bottom, width, height);
                result.Add(new PdfTextChar((char)Pdfium.FPDFText_GetUnicode(textHandle, i),
                    Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2),
                    Math.Max(0, Pdfium.FPDFText_GetFontSize(textHandle, i)) * _scale));
            }
            return result;
        }
    }

    private (int X, int Y) ToDevice(double x, double y, int width, int height)
    {
        if (Pdfium.FPDF_PageToDevice(handle, 0, 0, width, height, 0, x, y, out int deviceX, out int deviceY) == 0)
            throw new InvalidOperationException("The PDF character coordinates could not be read.");
        return (Math.Clamp(deviceX, 0, width - 1), Math.Clamp(deviceY, 0, height - 1));
    }

    internal unsafe void WriteImageToBuffer(byte[] buffer)
    {
        lock (Pdfium.SyncRoot)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            int width = GetPageWidth(), height = GetPageHeight();
            int length = checked(width * height * 4);
            if (buffer.Length < length) throw new ArgumentException("The PDF bitmap buffer is too small.", nameof(buffer));
            // PDFium retains this pointer only until the bitmap is destroyed, inside the fixed scope.
            fixed (byte* pointer = buffer)
            {
                using var bitmap = new PdfiumResource(Pdfium.FPDFBitmap_CreateEx(width, height, 4, pointer, checked(width * 4)), Pdfium.FPDFBitmap_Destroy);
                buffer.AsSpan(0, length).Clear();
                var matrix = new Pdfium.Matrix { A = (float)_scale, D = (float)_scale };
                var clip = new Pdfium.Rect { Right = width, Top = height };
                Pdfium.FPDF_RenderPageBitmapWithMatrix(bitmap.DangerousGetHandle(), handle, in matrix, in clip, 0);
            }
        }
    }

    private PdfiumResource LoadText() => new(Pdfium.FPDFText_LoadPage(handle), Pdfium.FPDFText_ClosePage);

    protected override bool ReleaseHandle()
    {
        lock (Pdfium.SyncRoot)
        {
            Pdfium.FPDF_ClosePage(handle);
            if (_ownsDocument) _document.DangerousRelease();
            return true;
        }
    }
}

internal sealed class PdfiumResource : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly Action<nint> _close;

    internal PdfiumResource(nint resource, Action<nint> close) : base(true)
    {
        _close = close;
        SetHandle(resource);
        if (IsInvalid) throw new InvalidOperationException("PDFium could not create a page resource.");
    }

    protected override bool ReleaseHandle()
    {
        lock (Pdfium.SyncRoot) _close(handle);
        return true;
    }
}

/// <summary>Only the PDFium APIs used by PDF input. Signatures follow public/fpdfview.h and fpdf_text.h.</summary>
internal static partial class Pdfium
{
    // PDFium is not thread-safe, even across documents. Do not destroy its process-wide library while consumers
    // may still own pages; individual SafeHandles release every document, page, text page and bitmap.
    internal static readonly object SyncRoot = new();
    private const string Library = "PaddleOcrNet.Pdfium";

    static Pdfium()
    {
        NativeLibrary.SetDllImportResolver(typeof(Pdfium).Assembly, Resolve);
        FPDF_InitLibrary();
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return 0;
        // Exact Unix basenames avoid the pdfium.so-first probing that caused #14.
        string file = OperatingSystem.IsWindows() ? "pdfium.dll"
            : OperatingSystem.IsMacOS() ? "libpdfium.dylib" : "libpdfium.so";
        return NativeLibrary.Load(file, assembly, searchPath);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Matrix { internal float A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { internal float Left, Top, Right, Bottom; }

    [LibraryImport(Library)] private static partial void FPDF_InitLibrary();
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint FPDF_LoadMemDocument(nint data, int length, string password);
    // FPDF_GetLastError returns unsigned long: 32 bits on Windows, pointer-sized on supported Unix targets.
    [LibraryImport(Library, EntryPoint = "FPDF_GetLastError")] internal static partial uint GetLastErrorWindows();
    [LibraryImport(Library, EntryPoint = "FPDF_GetLastError")] internal static partial nuint GetLastErrorUnix();
    [LibraryImport(Library)] internal static partial void FPDF_CloseDocument(nint document);
    [LibraryImport(Library)] internal static partial int FPDF_GetPageCount(nint document);
    [LibraryImport(Library)] internal static partial nint FPDF_LoadPage(nint document, int index);
    [LibraryImport(Library)] internal static partial void FPDF_ClosePage(nint page);
    [LibraryImport(Library)] internal static partial double FPDF_GetPageWidth(nint page);
    [LibraryImport(Library)] internal static partial double FPDF_GetPageHeight(nint page);
    [LibraryImport(Library)] internal static partial int FPDF_PageToDevice(nint page, int startX, int startY,
        int sizeX, int sizeY, int rotate, double pageX, double pageY, out int deviceX, out int deviceY);
    [LibraryImport(Library)] internal static unsafe partial nint FPDFBitmap_CreateEx(int width, int height, int format, byte* buffer, int stride);
    [LibraryImport(Library)] internal static partial void FPDFBitmap_Destroy(nint bitmap);
    [LibraryImport(Library)] internal static partial void FPDF_RenderPageBitmapWithMatrix(nint bitmap, nint page, in Matrix matrix, in Rect clipping, int flags);
    [LibraryImport(Library)] internal static partial nint FPDFText_LoadPage(nint page);
    [LibraryImport(Library)] internal static partial void FPDFText_ClosePage(nint text);
    [LibraryImport(Library)] internal static partial int FPDFText_CountChars(nint text);
    [LibraryImport(Library)] internal static unsafe partial int FPDFText_GetText(nint text, int start, int count, char* buffer);
    [LibraryImport(Library)] internal static partial uint FPDFText_GetUnicode(nint text, int index);
    [LibraryImport(Library)] internal static partial double FPDFText_GetFontSize(nint text, int index);
    [LibraryImport(Library)] internal static partial int FPDFText_GetCharBox(nint text, int index,
        out double left, out double right, out double bottom, out double top);
}
