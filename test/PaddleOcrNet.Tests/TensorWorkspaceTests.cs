using System.Runtime.InteropServices;
using PaddleOcrNet.Internal.Recognition;
using Xunit;

namespace PaddleOcrNet.Tests;

public class TensorWorkspaceTests
{
    [Fact]
    public void Reuse_clears_padding_and_expanded_slices_without_allocating_another_buffer()
    {
        using var workspace = new BoundedTensorWorkspace(1, 64);
        float[] first;
        using (var lease = workspace.Rent(64))
        {
            lease.Memory.Span.Fill(123);
            Assert.True(MemoryMarshal.TryGetArray((ReadOnlyMemory<float>)lease.Memory, out var segment));
            first = segment.Array!;
        }
        using (var shorter = workspace.Rent(8)) shorter.Memory.Span.Fill(456);
        using var expanded = workspace.Rent(64);
        Assert.All(expanded.Memory.ToArray(), value => Assert.Equal(0, value));
        Assert.True(MemoryMarshal.TryGetArray((ReadOnlyMemory<float>)expanded.Memory, out var reused));
        Assert.Same(first, reused.Array);
    }

    [Fact]
    public void Live_leases_are_isolated_and_capacity_is_strict_even_after_double_disposal()
    {
        using var workspace = new BoundedTensorWorkspace(2, 16);
        using var first = workspace.Rent(16);
        using var second = workspace.Rent(16);
        first.Memory.Span.Fill(7);
        Assert.All(second.Memory.ToArray(), value => Assert.Equal(0, value));
        Assert.Throws<InvalidOperationException>(() => workspace.Rent(16));
        second.Dispose();
        second.Dispose();
        using var third = workspace.Rent(16);
        Assert.Throws<InvalidOperationException>(() => workspace.Rent(16));
        Assert.Throws<ObjectDisposedException>(() => second.Memory);
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.Rent(17));
        workspace.Dispose();
        Assert.Throws<ObjectDisposedException>(() => workspace.Rent(16));
        Assert.All(first.Memory.ToArray(), value => Assert.Equal(7, value));
    }
}
