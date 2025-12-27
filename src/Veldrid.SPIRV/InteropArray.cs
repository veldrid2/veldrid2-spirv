using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Veldrid.SPIRV
{
    internal unsafe struct InteropArray<T> : IDisposable
        where T : unmanaged
    {
        public nuint Count;
        public T* Data;

        public InteropArray(nuint count, T* data)
        {
            Count = count;
            Data = data;
        }

        public InteropArray(nuint count)
        {
            Data = (T*) NativeMemory.AllocZeroed(checked(count * (uint) sizeof(T)));
            Count = count;
        }

        public readonly nuint ByteCount => checked(Count * (uint) sizeof(T));

        public readonly ref T this[nuint index]
        {
            get
            {
                if (index >= Count)
                {
                    ThrowOutOfRange();
                }
                return ref Data[index];
            }
        }

        public readonly ref T this[int index] => ref this[(uint) index];

        public readonly ref T Ref(int index) => ref this[(uint) index];

        public readonly ref T Ref(nuint index) => ref this[(uint) index];

        public readonly Span<T> AsSpan() => new(Data, checked((int) Count));

        public readonly InteropArray<T> Clone()
        {
            if (Data == null)
            {
                return this;
            }
            var result = new InteropArray<T>(Count);
            NativeMemory.Copy(Data, result.Data, ByteCount);
            return result;
        }

        public void Dispose()
        {
            if (Data != null)
            {
                NativeMemory.Free(Data);
                Data = null;
                Count = 0;
            }
        }

        [DoesNotReturn]
        private static void ThrowOutOfRange() => throw new ArgumentOutOfRangeException("index");
    }

    internal static class InteropArray
    {
        public static InteropArray<T> Clone<T>(ReadOnlySpan<T> span)
            where T : unmanaged
        {
            InteropArray<T> result = new((uint) span.Length);
            unsafe
            {
                span.CopyTo(new Span<T>(result.Data, span.Length));
            }
            return result;
        }

        public static unsafe InteropArray<byte> FromNullTerminated(byte* ptr)
        {
            // TODO: process larger strings...
            int length = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(ptr).Length;
            if (length < 0)
            {
                throw new ArgumentException();
            }
            return new InteropArray<byte>((uint) length, ptr);
        }

        public static unsafe InteropArray<byte> ToUtf8(ReadOnlySpan<char> value)
        {
            uint count = (uint) Util.UTF8.GetByteCount(value);
            var array = new InteropArray<byte>(count + 1);
            Util.UTF8.GetBytes(value, array.AsSpan());
            array[count] = (byte) '\0';
            return new InteropArray<byte>(count, array.Data);
        }

        public static string ToString(InteropArray<byte> array)
        {
            unsafe
            {
                return Util.UTF8.GetString(array.Data, checked((int) array.Count));
            }
        }
    }
}
