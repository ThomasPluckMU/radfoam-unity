
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

public static class NativeSliceExtensions
{
    public static unsafe T ReadAs<T>(this NativeSlice<byte> slice, int offset) where T : unmanaged
    {
        if (offset + sizeof(T) > slice.Length)
            throw new System.IndexOutOfRangeException("Offset out of bounds!");

        void* ptr = (byte*)slice.GetUnsafeReadOnlyPtr() + offset;
        return UnsafeUtility.ReadArrayElement<T>(ptr, 0);
    }


    public static unsafe void WriteAs<T>(this NativeSlice<byte> slice, int offset, T value) where T : unmanaged
    {
        if (offset + sizeof(T) > slice.Length)
            throw new System.IndexOutOfRangeException("Offset out of bounds!");

        void* ptr = (byte*)slice.GetUnsafePtr() + offset;
        UnsafeUtility.WriteArrayElement(ptr, 0, value);
    }
}