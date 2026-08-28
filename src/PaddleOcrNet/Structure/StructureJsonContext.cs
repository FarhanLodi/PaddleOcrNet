using System.Text.Json;
using System.Text.Json.Serialization;
using PaddleOcrNet.Models;

namespace PaddleOcrNet.Structure;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for the structure-export DTOs, so
/// <see cref="StructureResult.ToJson()"/> stays reflection-free / trim / Native-AOT safe.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(StructureResultDto))]
[JsonSerializable(typeof(StructureBlockDto))]
[JsonSerializable(typeof(IReadOnlyList<StructureBlockDto>))]
internal partial class StructureJsonContext : JsonSerializerContext
{
    /// <summary>
    /// The context <see cref="StructureResult.ToJson()"/> serializes through: same shape as
    /// <see cref="Default"/>, but with <see cref="PaddleOcrJson.Encoder"/> so non-ASCII recognized text
    /// is written verbatim instead of as <c>\uXXXX</c> escapes.
    /// </summary>
    internal static StructureJsonContext Unescaped { get; } = new(PaddleOcrJson.Options(indented: true));
}
