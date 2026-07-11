using Xunit;

namespace UvEcs.Tests;

public class ChunkPoolTests
{
    [Fact]
    public void Rented_buffer_has_room_for_chunk_plus_alignment()
    {
        var pool = new ChunkPool();
        var buf = pool.Rent();
        Assert.Equal(ChunkPool.ChunkBytes + ChunkPool.Alignment, buf.Length);
    }

    [Fact]
    public unsafe void Aligned_start_is_64_byte_aligned_and_leaves_a_full_chunk()
    {
        var pool = new ChunkPool();

        // POH выравнивает лишь на 8 байт, поэтому сдвиг обязателен и должен работать для любого буфера.
        for (int i = 0; i < 50; i++)
        {
            var buf = pool.Rent();
            nint start = ChunkPool.AlignedStart(buf);

            Assert.Equal(0, (int)(start & (ChunkPool.Alignment - 1)));

            fixed (byte* raw = buf)
            {
                int shift = (int)(start - (nint)raw);
                Assert.InRange(shift, 0, ChunkPool.Alignment - 1);
                Assert.True(shift + ChunkPool.ChunkBytes <= buf.Length,
                    "после сдвига в буфере не осталось места на полный чанк");
            }
        }
    }

    [Fact]
    public void Returned_buffer_is_reused()
    {
        var pool = new ChunkPool();
        var a = pool.Rent();
        pool.Return(a);
        var b = pool.Rent();

        Assert.Same(a, b);
        Assert.Equal(1, pool.TotalAllocated);
    }

    [Fact]
    public void Pool_counts_free_buffers()
    {
        var pool = new ChunkPool();
        var a = pool.Rent();
        var b = pool.Rent();
        Assert.Equal(0, pool.FreeCount);

        pool.Return(a);
        pool.Return(b);
        Assert.Equal(2, pool.FreeCount);
        Assert.Equal(2, pool.TotalAllocated);
    }

    [Fact]
    public void Returning_a_foreign_buffer_throws()
    {
        var pool = new ChunkPool();
        Assert.Throws<ArgumentException>(() => pool.Return(new byte[10]));
    }

    [Fact]
    public void Returning_the_same_buffer_twice_throws()
    {
        // Иначе следующие два Rent() отдадут одну память двум владельцам — молча.
        var pool = new ChunkPool();
        var buf = pool.Rent();
        pool.Return(buf);

        Assert.Throws<InvalidOperationException>(() => pool.Return(buf));
    }

    [Fact]
    public void Rent_reuses_a_returned_buffer_then_allocates_a_fresh_one()
    {
        // Первый Rent() после Return() обязан переиспользовать буфер, а следующий —
        // когда свободных не осталось — выделить новый, не тот же самый.
        var pool = new ChunkPool();
        var a = pool.Rent();
        pool.Return(a);

        var b = pool.Rent();
        var c = pool.Rent();

        Assert.Same(a, b);
        Assert.NotSame(b, c);
    }
}
