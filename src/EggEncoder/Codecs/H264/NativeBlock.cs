using System.Runtime.InteropServices;

namespace EggEncoder.Codecs.H264;

internal readonly unsafe struct NativeBlock
{
    public NativeBlock(int size)
    {
        Pointer = (byte*)NativeMemory.AllocZeroed((nuint)size);
        Size = size;
    }

    public byte* Pointer { get; }

    public int Size { get; }

    public T Read<T>(int offset)
        where T : unmanaged
    {
        RequireInside<T>(offset);
        return *(T*)(Pointer + offset);
    }

    public void Write<T>(int offset, T value)
        where T : unmanaged
    {
        RequireInside<T>(offset);
        *(T*)(Pointer + offset) = value;
    }

    public void WriteNativeULong(int offset, ulong value, int size)
    {
        if (size == 4)
        {
            Write(offset, checked((uint)value));
        }
        else
        {
            Write(offset, value);
        }
    }

    public void Free() => NativeMemory.Free(Pointer);

    private void RequireInside<T>(int offset)
        where T : unmanaged
    {
        if (offset < 0 || offset + sizeof(T) > Size)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }
}
