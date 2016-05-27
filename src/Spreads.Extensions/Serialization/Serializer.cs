﻿/*
    Copyright(c) 2014-2016 Victor Baybekov.

    This file is a part of Spreads library.

    Spreads library is free software; you can redistribute it and/or modify it under
    the terms of the GNU General Public License as published by
    the Free Software Foundation; either version 3 of the License, or
    (at your option) any later version.

    Spreads library is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.If not, see<http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Bootstrap;
using Newtonsoft.Json;
using Spreads.Collections;
using Spreads.Native;
using Newtonsoft.Json.Serialization;
using Newtonsoft.Json.Bson;
using System.IO;
using System.Collections.Concurrent;

namespace Spreads.Serialization {


    // TODO! this need big clean-up and optimization for an array pool
    // interesting idea is to use CWT to store a bool indicating if an array was 
    // takes from a pool - need to profile the performance of pool+CWT vs no pool
    // - could create a special array pool implementation for this

    /// <summary>
    /// 
    /// </summary>
    internal enum CompressionMethod {
        blosclz = 0,
        lz4 = 1,
        lz4hc = 2
        //, zlib = 3    // slow
        //, snappy = 4  // lz4 is better and easier to build, our Blosc does not include Snappy since it is C++, not C.
    }


    //http://stackoverflow.com/questions/10574645/the-fastest-way-to-check-if-a-type-is-blittable
    [Obsolete]
    internal static class BlittableHelper {
        public static object GetDefault(Type type) {

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        private static readonly Dictionary<Type, bool> Dic = new Dictionary<Type, bool>();
        private static readonly ConcurrentDictionary<Type, int> _cache = new ConcurrentDictionary<Type, int>();

        /// <summary>
        /// Assume "blittable" not only normally blittable, but those types for which Marshal.SizeOf doesn't throw and we could use StructureToPtr
        /// </summary>
        /// <param name="ty"></param>
        /// <returns></returns>
        [Obsolete]
        public static bool IsBlittable(this Type ty) {
            if (Dic.ContainsKey(ty)) {
                return Dic[ty];
            }

            try {
                // Class test
                var def = GetDefault(ty);
                if (def != null) {
                    // Non-blittable types cannot allocate pinned handle
                    GCHandle.Alloc(def, GCHandleType.Pinned).Free();
                    Dic[ty] = true;
                } else {
                    Dic[ty] = false;
                }
            } catch {
                try {
                    var size = Marshal.SizeOf(ty); // throws 
                    Dic[ty] = true;
                } catch {
                    Dic[ty] = false;
                }
            }
            return Dic[ty];
        }

        static BlittableHelper() {

        }

    }


    //TODO selfNoCopy is not needed? especially when buffers are not pooled
    /// <summary>
    /// Transform an intermediate byte array to T. During execution of this 
    /// delegate the source buffer if fixed in memory and pointer points to its first element.
    /// Usually, we use a closure with other object being modified or a database
    /// being written directly from the intermediate buffer, without creating another 
    /// copy, which is trimmed to actual data size, of the buffer.
    /// selfNoCopy means that the source could be used directly without copying, it is not a 
    /// temporary buffer but a final value
    /// </summary>
    [Obsolete("Too complex")]
    internal delegate T FixedBufferTransformer<T>(byte[] source, int length, bool selfNoCopy = false);

    // Serializer is static because
    // * easier to use without members etc
    // * DI could be done inside if needed, but static is like global singleton
    // * no versioning expected, it is a requirement to make serialization 
    //   as versatile as possible:
    //      - primitives/structs are just conversion to underlying bytes, will never change
    //      - string is always UTF8->bytes
    //      - everything else uses JSON, it is the most flexible and versioning could be 
    //          done via custom converters if needed


    /// <summary>
    /// Advanced generic serializer and compressor.
    /// </summary>
    public static class Serializer { // TODO internal

        internal class SerializerInstance : ISerializer {
            byte[] ISerializer.Serialize<T>(T value) {
                return Serializer.Serialize(value);
            }

            T ISerializer.Deserialize<T>(byte[] bytes) {
                return Serializer.Deserialize<T>(bytes);
            }


            byte[] ISerializer.Serialize(object obj0) {
                throw new NotImplementedException();
            }

            object ISerializer.Deserialize(byte[] obj0, Type obj1) {
                throw new NotImplementedException();
            }
        }

        // Uses native Blosc library for 
        // fast and efficient binary compression and fast copying of memory.

        static Serializer() {

            // This will ensure their static constructors are called before the serializer is used.
            ABI = Bootstrapper.ABI;
            // blosc threads
            NumThreads = Environment.ProcessorCount; // NB there are use cases when built-in chunking is needed
            CompressionMethod = CompressionMethod.lz4;
            Diff = true;
            ZeroByteArray = new byte[] { 0 };
            ObjectSerializer = new SpreadsJsonSerializer();
            ArrayCopyToNew = (source, length, self) => {
                if (self) {
                    Debug.Assert(source.Length == length);
                    return source;
                }
                // TODO cannot use pool here (no info how to return)
                var dest = new byte[length];
                Buffer.BlockCopy(source, 0, dest, 0, length);
                //Marshal.Copy(source, dest, 0, length);
                return dest;
            };

            Instance = new SerializerInstance();
        }

        public static ISerializer Instance { get; private set; }

        /// <summary>
        /// General-purpose serializer for reference types (ex. strings) and 
        /// non-blittable managed value types. This is a fallback
        /// serializer that is used when more advanced methods do not work.
        /// </summary>
        private static ISerializer ObjectSerializer { get; set; }

        private static ABI ABI { get; set; }
        private static int NumThreads { get; set; }
        private static bool Diff { get; set; }
        private static CompressionMethod CompressionMethod { get; set; }
        private static byte[] ZeroByteArray { get; set; }

        /// <summary>
        /// Transform serialization buffer
        /// </summary>
        [Obsolete]
        internal static FixedBufferTransformer<byte[]> ArrayCopyToNew { get; private set; }

#if PRERELEASE
        private static bool wroteSize = false;
#endif


        #region SortedMap compression

        /// <summary>
        /// 
        /// </summary>
        internal static byte[] CompressMap<K, V>(SortedMap<K, V> src,
            int? level = null, bool? shuffle = null,
            int? typeSize = null, CompressionMethod? method = null) {
            if (src == null) return null;
            if (src.size == 0) return EmptyArray<byte>.Instance;
            //return CompressMapSequential(src, level, shuffle, typeSize, method);
            return CompressMap(src, ArrayCopyToNew, level, shuffle, typeSize, method);
        }

        // TODO dynamic resolution fails sometimes, I don't understand why, this is a failover with reflection
        /// <summary>
        /// Used as unique name to call by reflection
        /// </summary>
        internal static byte[] CompressMap2<K, V>(SortedMap<K, V> src) {
            return CompressMap(src);
        }

        [Obsolete("Complexity of this yields only very small gain. We are copying arrays at later stage anyway. Find a way to avoid copy")]
        private static TResult CompressMap<K, V, TResult>(SortedMap<K, V> src,
           FixedBufferTransformer<TResult> transformer,
           int? level = null, bool? shuffle = null,
           int? typeSize = null, CompressionMethod? method = null) {

            int size = (int)src.size;
            int version = (int)src.Version;

            //src.CheckRegular(); // TODO? Need this?
            var isRegular = src.IsRegular;
            var isMutable = !src.IsReadOnly;
            FixedBufferTransformer<Tuple<byte[], int>> passThrough = (source, length, self) => {
                // NB! here was a real example of leaving fixed region with pointer and GC moving the object
                //unsafe
                //{
                //    fixed (byte* srcPtr = &source[0])
                //    {
                //        // !BUG leaving fixed with IntPtr
                //        return new Tuple<IntPtr, int>((IntPtr)srcPtr, length);
                //    };
                //}

                if (self) {
                    return Tuple.Create(source, length);
                } else {
                    byte[] dest = new byte[length];
                    Array.Copy(source, 0, dest, 0, length);
                    return Tuple.Create(dest, length);
                }
            };

            // NB lowcase internal field access, not properties. Null check if later will use IOrderedMap instead of map
            K[] keys = src.keys as K[] ?? src.Keys.ToArray();
            V[] values = src.values as V[] ?? src.Values.ToArray();

            // keys compression/serialization will normally be faster then values, start a task
            // which will normally be ready when values compressor is ready to call its transformer
            Task<Tuple<byte[], int>> cKeysTask;
            if (isRegular) {
                //var b1 = SerializeTransform(keys[0], passThrough);
                //var b2 = SerializeTransform(keys[1], passThrough);
                //var bytes = b1.Concat(b2).ToArray();
                //cKeysTask = Task.FromResult(bytes); // SerializeTransform(keys, passThrough));
                cKeysTask = Task.Run(() => CompressArray<K, Tuple<byte[], int>>(keys, passThrough, 0, 2, 0, shuffle, typeSize, method, Diff));
            } else {
                cKeysTask = Task.Run(() => CompressArray<K, Tuple<byte[], int>>(keys, passThrough, 0, size, level, shuffle, typeSize, method, Diff));
            }

            //var ms = new MemoryStream();
            //var bw = new BinaryWriter(ms);
            //bw.Write(size);
            //bw.Write(version);

            FixedBufferTransformer<TResult> buildReturnArray = (valBytes, valLen, self) => {
                var keyBytes = cKeysTask.Result.Item1;
                var keysLen = cKeysTask.Result.Item2; //keyBytes.Length;
                                                      // int len, int version, int values offset, keys, values
                var retLen = 4 + 4 + 4 + keysLen + valLen;
                byte[] ret = new byte[4 + 4 + 4 + keysLen + valLen];
                // use sign bit as a flag, regular vs irregular by definition do not expect a third option
                Buffer.BlockCopy(BitConverter.GetBytes(isRegular ? -size : size), 0, ret, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(isMutable ? version : -version), 0, ret, 4, 4); // immutable are marked with negative version
                Buffer.BlockCopy(BitConverter.GetBytes(keysLen + 4 + 4 + 4), 0, ret, 8, 4);
                Buffer.BlockCopy(keyBytes, 0, ret, 12, keysLen);
                Buffer.BlockCopy(valBytes, 0, ret, 12 + keysLen, valLen);
#if PRERELEASE
                if (!wroteSize) { // debug stuff
                    Console.WriteLine("SortedMap compressed size: " + retLen);
                    wroteSize = true;
                }
#endif
                return transformer(ret, retLen, true);

            };

            return CompressArray<V, TResult>(values, buildReturnArray, 0, size, level, shuffle, typeSize, method, Diff);
        }


        private static byte[] CompressMapSequential<K, V>(SortedMap<K, V> src,
           int? level = null, bool? shuffle = null,
           int? typeSize = null, CompressionMethod? method = null) {

            int size = (int)src.size;
            int version = (int)src.Version;

            //src.CheckRegular(); // TODO? Need this?
            var isRegular = src.IsRegular;
            var isMutable = !src.IsReadOnly;

            // NB lowcase internal field access, not properties. Null check if later will use IOrderedMap instead of map
            K[] keys = src.keys as K[] ?? src.Keys.ToArray();
            V[] values = src.values as V[] ?? src.Values.ToArray();

            // keys compression/serialization will normally be faster then values, start a task
            // which will normally be ready when values compressor is ready to call its transformer
            byte[] keyBytes;
            if (isRegular) {
                //var b1 = SerializeTransform(keys[0], passThrough);
                //var b2 = SerializeTransform(keys[1], passThrough);
                //var bytes = TODO ... simply save two keys
                // in general, we must prefix key size, with int it is 8 bytes for 2 keys, Blosc overhead is 16 bytes 
                keyBytes = CompressArray<K>(keys, 0, 2, 0, shuffle, typeSize, method, Diff);
            } else {
                keyBytes = CompressArray<K>(keys, 0, size, level, shuffle, typeSize, method, Diff);
            }

            Task<byte[]> valBytes = Task<byte[]>.Run(() => CompressArray<V>(values, 0, size, level, shuffle, typeSize, method, Diff));

            // int len, int version, int values offset, keys, values
            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);
            // use sign bit as a flag, regular vs irregular by definition do not expect a third option
            bw.Write(isRegular ? -size : size);
            bw.Write(isMutable ? version : -version);
            bw.Write(keyBytes.Length + 12);
            bw.Write(keyBytes);
            bw.Write(valBytes.Result);
            var ret = ms.ToArray();
#if PRERELEASE
            if (!wroteSize) { // debug stuff
                Console.WriteLine("SortedMap compressed size: " + ret.Length);
                wroteSize = true;
            }
#endif

            return ret;
        }


        ///// <summary>
        ///// Decompress a map from a byte array
        ///// </summary>
        //[Obsolete("Avoid copy, use pointers")]
        //public static SortedMap<K, V> DecompressMap<K, V>(byte[] src) {
        //    var size = BitConverter.ToInt32(src, 0);
        //    var isRegular = false;
        //    if (size < 0) {
        //        size = -size;
        //        isRegular = true;
        //    }
        //    var version = BitConverter.ToInt32(src, 4);
        //    var keyStart = 12;
        //    var valueStart = BitConverter.ToInt32(src, 8);
        //    var keysTask = Task.Run(() => DecompressArray<K>(src, keyStart, true));
        //    //var valuesTask = Task.Run(() => DecompressArray<V>(src, valueStart, true));
        //    var values = DecompressArray<V>(src, valueStart, true);
        //    keysTask.Wait();
        //    //Task.WaitAll(keysTask, valuesTask);
        //    Debug.Assert(keysTask.Result.Length == size);
        //    Debug.Assert(values.Length == size);
        //    var sm = SortedMap<K, V>.OfKeysAndValues(size, keysTask.Result, values);
        //    sm.Version = version;
        //    sm.isRegularKeys = isRegular;
        //    return sm;
        //    //var keys = Decompress<K>(src, keyStart, diff);
        //    //var values = Decompress<V>(src, valueStart, diff);
        //    //return SortedMap<K, V>.OfKeysAndValues(size, keys, values);
        //}

        /// <summary>
        /// Decompress a map from unmanaged pointer
        /// </summary>
        public static SortedMap<K, V> DecompressMapPtr<K, V>(IntPtr srcPtr) {
            var size = Marshal.ReadInt32(srcPtr, 0); //BitConverter.ToInt32(src, 0);
            var isRegular = false;
            var isMutable = true;
            if (size < 0) {
                size = -size;
                isRegular = true;
            }
            var version = Marshal.ReadInt32(srcPtr, 4); //BitConverter.ToInt32(src, 4);
            if (version < 0) {
                version = -version;
                isMutable = false;
            }
            var valueStart = Marshal.ReadInt32(srcPtr, 8); //BitConverter.ToInt32(src, 8);
            var keyStart = 12;
            //var keysTask = Task.Run(() => DecompressArray<K>(srcPtr, keyStart, true));
            K[] keys = null;
            keys = DecompressArray<K>(srcPtr, keyStart, Diff);
            //if (isRegular)
            //         {
            //             throw new NotImplementedException("Two values of K");
            //         }
            //         else
            //         {
            //             keys = DecompressArray<K>(srcPtr, keyStart, Diff);
            //         }

            var values = DecompressArray<V>(srcPtr, valueStart, Diff);
            //var valuesTask = Task.Run(() => DecompressArray<V>(srcPtr, valueStart, true));
            //Task.WaitAll(keysTask, valuesTask);
            //keysTask.Wait();
            //Debug.Assert(keysTask.Result.Length == size);
            //Debug.Assert(values.Length == size); // TODO should we trim?
            var sm = SortedMap<K, V>.OfSortedKeysAndValues(keys, values, size, KeyComparer.GetDefault<K>(), false, isRegular); //keysTask.Result
            sm.Version = version;
            sm.couldHaveRegularKeys = isRegular;
            return sm;
            //var keys = Decompress<K>(src, keyStart, diff);
            //var values = Decompress<V>(src, valueStart, diff);
            //return SortedMap<K, V>.OfKeysAndValues(size, keys, values);
        }

        //[DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
        //internal static extern int memcmp(IntPtr a, IntPtr b, long count);


        // TODO there could be a global optimization - store regular compressed keys 
        // in a separate table and use only a reference to them
        // Regular = every minute in an hour, every second in a minute, etc.
        // TimePeriod when used as a key should allow separation of 
        // hash and remainder, then inner keys will be equal for all regular series
        // This will be easier when most needed - second, milliseconds, etc.

        // Blosc with diff will already compress regular data by 99%, but for this:
        // * Calculate diff with iterations
        // * Compress
        // * Pack

        /// <summary>
        /// Very fast equality check of keys of two compressed maps, without decompression.
        /// Compression is 1-to-1 mapping, equality works on compressed values.
        /// Algos must be the same, if they are not, this function returns false even
        /// when decompressed keys are equal, so further comparison is needed.
        /// </summary>
        [Obsolete("Do not bother with compressed keys, for regular ones it is just an int64, others are rare")]
        internal static bool MapKeysAreCertainlyEqual<K, V>(IntPtr srcPtrA, IntPtr srcPtrB) {
            var sizeA = Marshal.ReadInt32(srcPtrA, 0);
            var sizeB = Marshal.ReadInt32(srcPtrB, 0);
            if (sizeA != sizeB) return false;
            return BytesExtensions.UnsafeCompare(srcPtrA + 12, srcPtrB + 12, sizeA);
        }


        #endregion


        #region Arrays compression


        /// <summary>
        /// Compress generic array using optimized parameters for each type
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="src"></param>
        /// <param name="start">Position in generic array to start from</param>
        /// <param name="length">Number of elements to take from the generic array for compression</param>
        /// <param name="level"></param>
        /// <param name="shuffle"></param>
        /// <param name="typeSize"></param>
        /// <param name="method"></param>
        /// <param name="diff"></param>
        /// <returns></returns>
        internal static byte[] CompressArray<T>(T[] src, int start = 0, int length = 0,
            int? level = null, bool? shuffle = null,
            int? typeSize = null, CompressionMethod? method = null, bool diff = false) {
            if (src == null) return null;
            if (src.Length == 0) return EmptyArray<byte>.Instance;
            return CompressArray<T, byte[]>(src, ArrayCopyToNew, start, length, level, shuffle, typeSize, method, diff);
        }

        internal static TResult CompressArray<T, TResult>(T[] src,
            FixedBufferTransformer<TResult> bufferTransform,
            int start = 0, int length = 0,
            int? level = null, bool? shuffle = null,
            int? typeSize = null, CompressionMethod? method = null, bool diff = false) {

            if (src == null) throw new ArgumentNullException("src");
            if (src.Length == 0) throw new ArgumentException("src is empty");
            if (start < 0 || (src.Length > 0 && start > src.Length - 1)) throw new ArgumentException("wrong offset");
            if (length < 0 || length > src.Length - start) throw new ArgumentException("wrong offset");
            if (length == 0) length = src.Length - start; // default length

            var level1 = level ?? 9;
            var shuffle1 = shuffle ?? true;
            var typeSize1 = typeSize ?? 0;
            var method1 = method ?? CompressionMethod.lz4;

            var ty = typeof(T);

            //try {
            // we really care about these cases, for other cases JSON.NET is just good enough
            if (ty.IsValueType && !ty.IsGenericType &&
            (ty.IsLayoutSequential || ty.IsExplicitLayout
             || ty == typeof(DateTimeOffset) || ty == typeof(DateTime))
            ) {
                unsafe
                {
                    checked {
                        if (false)
                        {

                            // this bastards have Auto layout for no reason http://stackoverflow.com/a/21883421/801189
                        }
                        else if (ty == typeof (DateTime))
                        {
                            //	#region unsafe version, use only if performance boost is proven

                            //	//typeSize1 = 8;
                            //	//var maxLength = length * typeSize1 + 16;
                            //	//// TODO dest take from buffer pool here
                            //	//var dest = new byte[maxLength];
                            //	//var destSize = new UIntPtr((uint)maxLength);
                            //	//var dtSrc = (DateTime[])(object)src;
                            //	//fixed (DateTime* dtSrcPtr = &dtSrc[start])
                            //	//fixed (byte* destPtr = &dest[0])
                            //	//{
                            //	//	var compSize = NativeMethods.blosc_compress_ctx(
                            //	//		new IntPtr(level1), new IntPtr(shuffle1 ? 1 : 0),
                            //	//		new UIntPtr((uint)typeSize1),
                            //	//		new UIntPtr((uint)(length * typeSize1)),
                            //	//		(byte*)dtSrcPtr, destPtr, destSize,
                            //	//		method1.ToString(), new UIntPtr((uint)0),
                            //	//		NumThreads
                            //	//		);
                            //	//	if (compSize <= 0) throw new ApplicationException("Invalid compression input");
                            //	//	var ret = new byte[compSize];
                            //	//	Array.Copy(dest, ret, compSize);
                            //	//	// TODO dest return to pool here
                            //	//	return ret;
                            //	//}


                            #endregion
                            var srcDt = src as DateTime[];
                            Int64[] newSrc = new Int64[length];

                            // NB this is slow on benchmarks
                            //Parallel.For(0, length, (i) => {
                            //    newSrc[i] = srcDt[i].ToInt64();// Convert.ToDecimal(src[i]);
                            //});
                            //return CompressArray<Int64, TResult>(newSrc, bufferTransform, 0,
                            //    length, level1, shuffle1, sizeof(Int64), method1, diff); // NB always false if "if" block is not commented out

                            var previous = 0L;
                            for (int i = start; i < length; i++) {
                                var dt = srcDt[i];
                                var dateData = dt.ToInt64();
                                newSrc[i - start] = dateData - previous;
                                if (diff) previous = dateData;
                            }
                            return CompressArray<long, TResult>(newSrc, bufferTransform, 0, length, level1, shuffle1, sizeof(ulong), method1,
                                false); // NB always false
                        }
                        else if (ty == typeof (DateTimeOffset))
                        {
                            typeSize1 = 12;
                            throw new NotImplementedException();
                            //} else if (ty == typeof(double)) {
                            //	// we rarely compress doubles and store them as decimals
                            //	// usually we store original data as decimals to avoid precision loss, but if we then request data as doubles
                            //	// we convert decimals to doubles
                            //	var srcDbl = src as double[];

                            //	//save allocation of decimal by precomputing diff and calling CompressArray with diff = false
                            //	//if (diff) {
                            //	//    decimal[] newSrc = new decimal[srcDbl.Length];
                            //	//    var previous = 0M;
                            //	//    for (int i = start; i < srcDbl.Length; i++) {
                            //	//        var dec = Convert.ToDecimal(srcDbl[i]); // (decimal);
                            //	//        newSrc[i - start] = dec - previous;
                            //	//        previous = dec;
                            //	//    }
                            //	//    return CompressArray<decimal, TResult>(newSrc, bufferTransform, 0, length, level1, shuffle1,
                            //	//        sizeof(decimal), method1, false); // NB always false
                            //	//} else {
                            //	//decimal[] newSrc = new decimal[length];
                            //	//Parallel.For(0, length, (i) => {
                            //	//	newSrc[i] = (decimal)srcDbl[i];// Convert.ToDecimal(src[i]);
                            //	//});
                            //	return CompressArray<double, TResult>(srcDbl, bufferTransform, 0,
                            //		length, level1, shuffle1, sizeof(double), method1, diff); // NB always false if "if" block is not commented out
                            //																   //}

                            //} else if (ty == typeof(decimal) && diff) {
                            //	var srcDec = src as decimal[];

                            //	//var previous = 0M;
                            //	//for (int i = start; i < length; i++) {
                            //	//    var val = srcDec[i];
                            //	//    newSrc[i - start] = val - previous;
                            //	//    previous = val;
                            //	//}
                            //	return CompressArray<decimal, TResult>(srcDec, bufferTransform, 0, length, level1, shuffle1,
                            //		sizeof(long), method1, false); // NB always false
                            //} else if (ty == typeof(long) && diff) {


                            //	long[] newSrc = new long[length];
                            //	var srcLong = src as long[];
                            //	newSrc[0] = srcLong[0];
                            //	//Yeppp.Core.Subtract_V64sV64s_V64s(srcLong, 1, srcLong, 0, newSrc, 1, length - 1);
                            //	Parallel.For(1, length, (i) => {
                            //		newSrc[i] = srcLong[i] - srcLong[i - 1];// Convert.ToDecimal(src[i]);
                            //	});

                            //	//var previous = 0L;
                            //	//for (int i = start; i < length; i++) {
                            //	//    var val = srcLong[i];
                            //	//    newSrc[i - start] = val - previous;
                            //	//    previous = val;
                            //	//}
                            //	return CompressArray<long, TResult>(newSrc, bufferTransform, 0, length, level1, shuffle1,
                            //		sizeof(long), method1, false); // NB always false
                        }
                        else
                        {
                            //                 if (length == 0)
                            //                 {
                            //var buf = new byte[16];
                            //                  fixed (byte* bufferPtr = &buf[0])
                            //                  {
                            //                   var compSize = NativeMethods.blosc_compress_ctx(
                            //                    new IntPtr(level1), new IntPtr(shuffle1 ? 1 : 0),
                            //                    new UIntPtr((uint) typeSize1),
                            //                    new UIntPtr((uint) (length*typeSize1)),
                            //                    IntPtr.Zero, new IntPtr(bufferPtr), (UIntPtr)16,
                            //                    method1.ToString(), new UIntPtr((uint) 0),
                            //                    NumThreads
                            //                    );
                            //	if (compSize <= 0) throw new ApplicationException("Invalid compression input");
                            //	return bufferTransform(buf, compSize);
                            //}
                            //                 }
                            typeSize1 = Marshal.SizeOf(src[0]);
                            /// Blosc header 16 bytes
                            var maxLength = length*typeSize1 + 16;
                            // TODO! (perf) use pool
                            var buffer = new byte[maxLength];
                            var bufferSize = new UIntPtr((uint) maxLength);
                            try
                            {
                                using (var gp = new GenericArrayPinner<T>(src))
                                {
                                    IntPtr srcPtr = gp.GetNthPointer(start);
                                    // TODO if cannot pin, use a buffer (from a pool) for source and Marshal.StructureToPtr to fill the buffer via pointers

                                    fixed (byte* bufferPtr = &buffer[0])
                                    {
                                        var compSize = NativeMethods.blosc_compress_ctx(
                                            new IntPtr(level1), new IntPtr(shuffle1 ? 1 : 0),
                                            new UIntPtr((uint) typeSize1),
                                            new UIntPtr((uint) (length*typeSize1)),
                                            srcPtr, new IntPtr(bufferPtr), bufferSize,
                                            "lz4", new UIntPtr((uint) 0),
                                            NumThreads
                                            );
                                        if (compSize <= 0) throw new ApplicationException("Invalid compression input");
                                        return bufferTransform(buffer, compSize);
                                    }
                                }
                            }
                            catch
                            {
                                var srcLength = length*typeSize1;
                                // TODO! (perf) use pool
                                var scrBuffer = new byte[srcLength];

                                fixed (byte* srcBufferPtr = &scrBuffer[0])
                                {
                                    // fill scrBuffer with Marshal.StructureToPtr
                                    for (int i = start; i < length + start; i++)
                                    {
                                        Marshal.StructureToPtr(src[i], (IntPtr) (&srcBufferPtr[i*typeSize1]), false);
                                    }

                                    fixed (byte* bufferPtr = &buffer[0])
                                    {
                                        var compSize = NativeMethods.blosc_compress_ctx(
                                            new IntPtr(level1), new IntPtr(shuffle1 ? 1 : 0),
                                            new UIntPtr((uint) typeSize1),
                                            new UIntPtr((uint) (length*typeSize1)),
                                            (IntPtr) srcBufferPtr, new IntPtr(bufferPtr), bufferSize,
                                            "lz4", new UIntPtr((uint) 0),
                                            NumThreads
                                            );
                                        if (compSize <= 0) throw new ApplicationException("Invalid compression input");
                                        return bufferTransform(buffer, compSize);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            //} catch (Exception) { // TODO catch only specific errors e.g. when type is non-blittable

            //}

            var bytes = ObjectSerializer.Serialize(src);
            return CompressBytes<TResult>(bytes, bufferTransform, level1, false, 1, method1);

        }


        internal static unsafe T[] DecompressArrayDefault<T>(IntPtr srcPtr) {
            return DecompressArray<T>(srcPtr);
        }


        /// <summary>
        /// 
        /// </summary>
        public static unsafe T[] DecompressArray<T>(byte[] src, int start = 0, bool diff = false) {
            if (src == null) return null; // throw new ArgumentNullException("src");
            if (src.Length == 0) return EmptyArray<T>.Instance; // throw new ArgumentException("src is empty");
            fixed (byte* srcPtr = &src[start])
            {
                return DecompressArray<T>((IntPtr)srcPtr, 0, diff);
            }
        }


        /// <summary>
        /// 
        /// </summary>
        internal static unsafe T[] DecompressArray<T>(IntPtr srcPtr, int start = 0, bool diff = false) {

            var ty = typeof(T);
            var typeSize1 = 0;

            //try {
            if (ty.IsValueType && !ty.IsGenericType &&
                (ty.IsLayoutSequential || ty.IsExplicitLayout
                 || ty == typeof(DateTimeOffset) || ty == typeof(DateTime))
                ) {
                if (false) {
                } else if (ty == typeof(DateTime)) {
                    Int64[] newSrc = DecompressArray<Int64>(srcPtr, start, false); // avoid lon[] allocation, calc diffs in the loop
                    DateTime[] dts = new DateTime[newSrc.Length];
                    var previous = 0L;
                    for (int i = 0; i < newSrc.Length; i++) {
                        var current = newSrc[i] + previous;
                        dts[i] = current.ToDateTime();
                        if (diff) previous = current;
                    }
                    return (T[])(object)dts;
                    //} else if (ty == typeof(DateTimeOffset)) {
                    //    typeSize1 = 12;
                    //    throw new NotImplementedException();
                    //} else if (ty == typeof(double)) {
                    //	decimal[] newSrc = DecompressArray<decimal>(srcPtr, start, diff);
                    //	//double[] doubles = new double[newSrc.Length];
                    //	//var previous = 0M;
                    //	//unsafe
                    //	//{
                    //	//    fixed (decimal* newSrcPtr = &newSrc[0])
                    //	//    fixed (double* doublePtr = &doubles[0])
                    //	//    {
                    //	//        for (int i = 0; i < newSrc.Length; i++) {
                    //	//            var current = newSrcPtr[i] + previous;
                    //	//            doublePtr[i] = Convert.ToDouble(current);
                    //	//            newSrcPtr[i] = newSrcPtr[i] + previous;
                    //	//            if (diff) previous = current;
                    //	//        }
                    //	//        return (T[])(object)doubles;
                    //	//    }
                    //	//}
                    //	double[] doubles = new double[newSrc.Length];
                    //	//if (diff) {
                    //	//    var previous = 0M;
                    //	//    for (int i = 0; i < doubles.Length; i++) {
                    //	//        var current = newSrc[i] + previous;
                    //	//        doubles[i] = decimal.ToDouble(current);// Convert.ToDouble(current);
                    //	//        if (diff) previous = current;
                    //	//    }
                    //	//} else {
                    //	Parallel.For(0, doubles.Length, (i) => {
                    //		doubles[i] = decimal.ToDouble(newSrc[i]);// Convert.ToDouble(newSrc[i]);
                    //	});
                    //	//}
                    //	return (T[])(object)doubles;
                    //} else if (ty == typeof(decimal) && diff) {
                    //    decimal[] newSrc = DecompressArray<decimal>(srcPtr, start, false); // NB always false
                    //    unsafe
                    //    {
                    //        fixed (decimal* newSrcPtr = &newSrc[0])
                    //        {
                    //            for (int i = 1; i < newSrc.Length; i++) {
                    //                newSrcPtr[i] = newSrcPtr[i] + newSrcPtr[i - 1];
                    //            }
                    //            return (T[])(object)newSrc;
                    //        }
                    //    }

                    //} else if (ty == typeof(long) && diff) {
                    //    long[] newSrc = DecompressArray<long>(srcPtr, start, false); // NB always false
                    //    var previous = 0L;
                    //    for (int i = 0; i < newSrc.Length; i++) {
                    //        var current = newSrc[i] + previous;
                    //        newSrc[i] = current;
                    //        previous = current;
                    //    }
                    //    return (T[])(object)newSrc;
                } else {
                    typeSize1 = Marshal.SizeOf(ty);
                    unsafe
                    {
                        byte* srcPtr2 = &(((byte*)srcPtr)[start]);

                        var nbytes = new UIntPtr();
                        var cbytes = new UIntPtr();
                        var blocksize = new UIntPtr();
                        NativeMethods.blosc_cbuffer_sizes(
                            (IntPtr)srcPtr2, ref nbytes, ref cbytes, ref blocksize);
                        var dest = new T[(int)(nbytes.ToUInt32()) / typeSize1];
                        try {
                            using (var gp = new GenericArrayPinner<T>(dest)) {
                                IntPtr destPtr = gp.GetNthPointer(0);

                                var decompSize = NativeMethods.blosc_decompress_ctx(
                                    (IntPtr)srcPtr2, destPtr, nbytes, NumThreads);

                                // Do we need a use case when we need a transformed value 
                                // without original value? 
                                // e.g. log(x) 

                                if (decompSize <= 0) throw new ApplicationException("Invalid compression input");
                                return dest;
                            }
                        } catch {
                            // TODO! (perf) use pool
                            var destBuffer = new byte[(int)nbytes];
                            fixed (byte* destPtr = &destBuffer[0])
                            {
                                var decompSize = NativeMethods.blosc_decompress_ctx(
                                    (IntPtr)srcPtr2, (IntPtr)destPtr, nbytes, NumThreads);
                                if (decompSize <= 0) throw new ApplicationException("Invalid compression input");

                                for (int i = 0; i < dest.Length; i++) {
                                    var currentPtr = (IntPtr)(&destPtr[i * typeSize1]);
                                    var currItem = Marshal.PtrToStructure(currentPtr, ty);
                                    dest[i] = (T)currItem;
                                }
                                return dest;
                            }



                            //fixed (byte* srcBufferPtr = &scrBuffer[0])
                            //{
                            //    // fill scrBuffer with Marshal.StructureToPtr
                            //    for (int i = start; i < length + start; i++) {
                            //        Marshal.StructureToPtr(src[i], (IntPtr)(&srcBufferPtr[i * typeSize1]), false);
                            //    }

                            //    fixed (byte* bufferPtr = &buffer[0])
                            //    {
                            //        var compSize = NativeMethods.Library.blosc_compress_ctx(
                            //            new IntPtr(level1), new IntPtr(shuffle1 ? 1 : 0),
                            //            new UIntPtr((uint)typeSize1),
                            //            new UIntPtr((uint)(length * typeSize1)),
                            //            (IntPtr)srcBufferPtr, new IntPtr(bufferPtr), bufferSize,
                            //            method1.ToString(), new UIntPtr((uint)0),
                            //            numThreads
                            //            );
                            //        if (compSize <= 0) throw new ApplicationException("Invalid compression input");
                            //        return bufferTransform(buffer, compSize);
                            //    }
                            //}


                        }
                    }
                }
            }
            //} catch (Exception) { // TODO catch only specific errors e.g. when type is non-blittable

            //}
            byte* srcPtr3 = &(((byte*)srcPtr)[start]);
            byte[] bytes = DecompressBytes((IntPtr)srcPtr3);
            return ObjectSerializer.Deserialize<T[]>(bytes);
        }

        //#endregion


        #region byte[] to byte[] compression


        /// <summary>
        /// Compress byte array
        /// </summary>
        internal static byte[] CompressBytes(byte[] src,
            int compressionLevel = 9, bool shuffle = true,
            int typeSize = 1, CompressionMethod method = CompressionMethod.lz4) {
            if (src.Length == 0) return EmptyArray<byte>.Instance;
            return CompressBytes(src, ArrayCopyToNew, compressionLevel, shuffle, typeSize, method);
        }

        /// <summary>
        /// Compress byte array
        /// </summary>
        internal static TResult CompressBytes<TResult>(byte[] src,
            FixedBufferTransformer<TResult> transformer,
            int compressionLevel = 9, bool shuffle = true,
            int typeSize = 1, CompressionMethod method = CompressionMethod.lz4) {

            if (compressionLevel < 0 || compressionLevel > 9) throw new ArgumentOutOfRangeException("Level must be in 0-9 range");
            if (typeSize < 1 || (typeSize > 255 && shuffle)) throw new ArgumentOutOfRangeException("TypeSize must be positive and <256 if shuffle is set to true");
            var compressor = "lz4";

            if (src == null) throw new ArgumentNullException("src");
            if (!(new[] { "blosclz", "lz4", "lz4hc", "zlib" }.Contains(compressor))) {
                throw new ArgumentOutOfRangeException("compressor");
            }

            var maxLength = src.Length + 16;
            // TODO! (perf) use pool
            var dest = new byte[maxLength];
            var destSize = new UIntPtr((uint)maxLength);
            unsafe
            {
                fixed (byte* srcPtr = &src[0])
                fixed (byte* detPtr = &dest[0])
                {
                    var compSize = NativeMethods.blosc_compress_ctx(
                        new IntPtr(compressionLevel), new IntPtr(shuffle ? 1 : 0),
                        new UIntPtr((uint)typeSize),
                        new UIntPtr((uint)src.Length),
                        (IntPtr)srcPtr, (IntPtr)detPtr, destSize,
                        compressor, new UIntPtr((uint)0),
                        NumThreads
                        );
                    if (compSize <= 0) throw new ApplicationException("Invalid compression input");
                    // compression adds a header, without the header decompression could detect that a byte array is not compressed
                    //if (compSize >= src.Length) {
                    //    // will copy self in case of ArrayCopy
                    //    return transformer(src, src.Length, true);
                    //} else {
                    // TODO! (perf) use pool
                    var ret = new byte[compSize];
                    Array.Copy(dest, ret, compSize);
                    return transformer(dest, compSize);
                    //}
                }
            }
        }

        /// <summary>
        /// Decompress byte array
        /// </summary>
        public static byte[] DecompressBytes(byte[] src) {
            if (src == null) throw new ArgumentNullException("src");
            unsafe
            {
                fixed (byte* srcPtr = &src[0])
                {
                    return DecompressBytes((IntPtr)srcPtr, src.Length)
                        ?? src; // null -> was not compressed
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="srcPtr"></param>
        /// <param name="srcSize"></param>
        /// <returns>null if source is not compressed</returns>
        internal static byte[] DecompressBytes(IntPtr srcPtr, int srcSize = 0) {
            unsafe
            {
                byte[] dest;

                var nbytes = new UIntPtr();
                var cbytes = new UIntPtr();
                var blocksize = new UIntPtr();

                NativeMethods.blosc_cbuffer_sizes(
                    (IntPtr)srcPtr, ref nbytes, ref cbytes, ref blocksize);

                if (((int)nbytes == 0 || (int)cbytes == 0) && srcSize > 0) {
                    return null;
                }

                if (srcSize > 0 && srcSize != (int)(cbytes.ToUInt32()))
                    throw new ArgumentOutOfRangeException("Wrong src size");
                // TODO! (perf) use pool
                dest = new byte[(int)(nbytes.ToUInt32())];
                fixed (byte* detPtr = &dest[0])
                {
                    var decompSize = NativeMethods.blosc_decompress_ctx(
                        (IntPtr)srcPtr, (IntPtr)detPtr, nbytes, NumThreads);
                    if (decompSize <= 0) throw new ApplicationException("Invalid compression input");
                    return dest;
                }
            }
        }

        #endregion


        #region Generic Serialization

        internal static byte[] SerializeImpl<UKey, UValue>(SortedMap<UKey, UValue> map) {
            return CompressMap(map);
        }
        internal static TResult SerializeTransformImpl<UKey, UValue, TResult>(SortedMap<UKey, UValue> map,
            FixedBufferTransformer<TResult> transformer) {
            return CompressMap(map, transformer);
        }

        internal static byte[] SerializeImpl<U>(U[] array) {
            return CompressArray(array);
        }

        internal static TResult SerializeTransformImpl<U, TResult>(U[] array,
            FixedBufferTransformer<TResult> transformer) {
            return CompressArray(array, transformer);
        }

        internal static byte[] SerializeImpl(double value) {
            return BitConverter.GetBytes(value);
        }
        internal static TResult SerializeTransformImpl<TResult>(double value,
            FixedBufferTransformer<TResult> transformer) {
            var bytes = SerializeImpl(value);
            unsafe
            {
                fixed (byte* bytesPtr = &bytes[0])
                {
                    return transformer(bytes, bytes.Length, true);
                }
            }
        }

        internal static byte[] SerializeImpl(decimal value) {
            unsafe
            {
                var dest = new byte[16];
                fixed (byte* destPtr = dest)
                {
                    *((decimal*)destPtr) = value;
                    return dest;
                }
            }
        }

        internal static TResult SerializeTransformImpl<TResult>(decimal value,
            FixedBufferTransformer<TResult> transformer) {
            var bytes = SerializeImpl(value);
            unsafe
            {
                fixed (byte* bytesPtr = &bytes[0])
                {
                    return transformer(bytes, bytes.Length, true);
                }
            }
        }

        internal static byte[] SerializeImpl(long value) {
            return BitConverter.GetBytes(value);
        }
        internal static TResult SerializeTransformImpl<TResult>(long value,
            FixedBufferTransformer<TResult> transformer) {
            var bytes = SerializeImpl(value);
            unsafe
            {
                fixed (byte* bytesPtr = &bytes[0])
                {
                    return transformer(bytes, bytes.Length, true);
                }
            }
        }

        internal static byte[] SerializeImpl(ulong value) {
            return BitConverter.GetBytes(value);
        }
        internal static TResult SerializeTransformImpl<TResult>(ulong value,
            FixedBufferTransformer<TResult> transformer) {
            var bytes = SerializeImpl(value);
            unsafe
            {
                fixed (byte* bytesPtr = &bytes[0])
                {
                    return transformer(bytes, bytes.Length, true);
                }
            }
        }

        internal static byte[] SerializeImpl(int value) {
            return BitConverter.GetBytes(value);
        }
        internal static TResult SerializeTransformImpl<TResult>(int value,
            FixedBufferTransformer<TResult> transformer) {
            var bytes = SerializeImpl(value);
            unsafe
            {
                fixed (byte* bytesPtr = &bytes[0])
                {
                    return transformer(bytes, bytes.Length, true);
                }
            }
        }

        internal static byte[] SerializeImpl(uint value) {
            return BitConverter.GetBytes(value);
        }
        internal static TResult SerializeTransformImpl<TResult>(uint value,
            FixedBufferTransformer<TResult> transformer) {
            var bytes = SerializeImpl(value);
            unsafe
            {
                fixed (byte* bytesPtr = &bytes[0])
                {
                    return transformer(bytes, bytes.Length, true);
                }
            }
        }




        internal static byte[] SerializeImpl(short value) {
            return BitConverter.GetBytes(value);
        }
        internal static TResult SerializeTransformImpl<TResult>(short value,
            FixedBufferTransformer<TResult> transformer) {
            var bytes = SerializeImpl(value);
            unsafe
            {
                fixed (byte* bytesPtr = &bytes[0])
                {
                    return transformer(bytes, bytes.Length, true);
                }
            }
        }


        internal static byte[] SerializeImpl(ushort value) {
            return BitConverter.GetBytes(value);
        }
        internal static TResult SerializeTransformImpl<TResult>(ushort value,
            FixedBufferTransformer<TResult> transformer) {
            var bytes = SerializeImpl(value);
            unsafe
            {
                fixed (byte* bytesPtr = &bytes[0])
                {
                    return transformer(bytes, bytes.Length, true);
                }
            }
        }


        internal static byte[] SerializeImpl(byte value) {
            return BitConverter.GetBytes(value);
        }
        internal static TResult SerializeTransformImpl<TResult>(byte value,
            FixedBufferTransformer<TResult> transformer) {
            var bytes = SerializeImpl(value);
            unsafe
            {
                fixed (byte* bytesPtr = &bytes[0])
                {
                    return transformer(bytes, bytes.Length, true);
                }
            }
        }


        internal static byte[] SerializeImpl(sbyte value) {
            return BitConverter.GetBytes(value);
        }
        internal static TResult SerializeTransformImpl<TResult>(sbyte value,
            FixedBufferTransformer<TResult> transformer) {
            var bytes = SerializeImpl(value);
            unsafe
            {
                fixed (byte* bytesPtr = &bytes[0])
                {
                    return transformer(bytes, bytes.Length, true);
                }
            }
        }


        internal static byte[] SerializeImpl(bool value) {
            return BitConverter.GetBytes(value);
        }
        internal static TResult SerializeTransformImpl<TResult>(bool value,
            FixedBufferTransformer<TResult> transformer) {
            var bytes = SerializeImpl(value);
            unsafe
            {
                fixed (byte* bytesPtr = &bytes[0])
                {
                    return transformer(bytes, bytes.Length, true);
                }
            }
        }

        internal static byte[] SerializeImpl(char value) {
            return BitConverter.GetBytes(value);
        }
        internal static TResult SerializeTransformImpl<TResult>(char value,
            FixedBufferTransformer<TResult> transformer) {
            var bytes = SerializeImpl(value);
            unsafe
            {
                fixed (byte* bytesPtr = &bytes[0])
                {
                    return transformer(bytes, bytes.Length, true);
                }
            }
        }


        internal static byte[] SerializeImpl(float value) {
            return BitConverter.GetBytes(value);
        }
        internal static TResult SerializeTransformImpl<TResult>(float value,
            FixedBufferTransformer<TResult> transformer) {
            var bytes = SerializeImpl(value);
            unsafe
            {
                fixed (byte* bytesPtr = &bytes[0])
                {
                    return transformer(bytes, bytes.Length, true);
                }
            }
        }


        internal static byte[] SerializeImpl(DateTime value) {
            return SerializeImpl(value.ToBinary());
        }

        internal static TResult SerializeTransformImpl<TResult>(DateTime value,
            FixedBufferTransformer<TResult> transformer) {
            var bytes = SerializeImpl(value);
            unsafe
            {
                fixed (byte* bytesPtr = &bytes[0])
                {
                    return transformer(bytes, bytes.Length, true);
                }
            }
        }


        internal static byte[] SerializeImpl(byte[] value) {
            if (value.Length == 0) return EmptyArray<byte>.Instance;
            return CompressBytes(value, 9, false, 1, CompressionMethod);
        }
        internal static TResult SerializeTransformImpl<TResult>(byte[] value,
            FixedBufferTransformer<TResult> transformer) {
            return CompressBytes(value, transformer, 9, false, 1, CompressionMethod);
        }

        internal static byte[] SerializeImpl<T>(T value) { //where T : struct
            var ty = value.GetType();
            if (ty.IsValueType && !ty.IsGenericType && (ty.IsLayoutSequential || ty.IsExplicitLayout)) {
                unsafe
                {
                    var typeSize = Marshal.SizeOf(value);
                    var dest = new byte[typeSize];
                    fixed (byte* destPtr = &dest[0])
                    {
                        // TODO avoid Marshal.StructureToPtr? to play safe?
                        Marshal.StructureToPtr(value, (IntPtr)destPtr, false);
                        return dest;
                    }
                }
                //}
            } else if (ty.IsGenericType &&
                       ty.GetGenericTypeDefinition() == typeof(SortedMap<,>)) {
                // TODO cache this method like in deser

                //if (!true) {
                var genericArgs = ty.GetGenericArguments();
                var keyType = genericArgs[0];
                var valueType = genericArgs[1];
                var mi = typeof(Serializer).GetMethod("CompressMap2",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                var genericMi = mi.MakeGenericMethod(keyType, valueType);
                //genericMethods[ty] = genericMi;
                return (byte[])genericMi.Invoke(null, new object[] { (object)value });
                //}
            } else if (ty.IsGenericType &&
                       ty.GetGenericTypeDefinition() == typeof(Series<,>)) {

                // TODO cache this method like in deser

                var genericArgs = ty.GetGenericArguments();
                var keyType = genericArgs[0];
                var valueType = genericArgs[1];
                var mi = typeof(SeriesExtensions).GetMethod("ToSortedMap",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                var genericMi = mi.MakeGenericMethod(keyType, valueType);
                //genericMethods[ty] = genericMi;
                var sm = genericMi.Invoke(null, new object[] { (object)value });

                return Serialize(sm);
            } else {
                return SerializeImplObj((object)value);
            }
        }

        internal static TResult SerializeTransformImpl<TResult, TStruct>(TStruct value,
            FixedBufferTransformer<TResult> transformer) where TStruct : struct {
            var ty = typeof(TStruct);
            if (ty.IsLayoutSequential || ty.IsExplicitLayout) {
                unsafe
                {
                    var typeSize = Marshal.SizeOf(value);
                    var dest = new byte[typeSize];
                    fixed (byte* destPtr = &dest[0])
                    {
                        return transformer(dest, typeSize);
                    }
                }
            } else {
                return SerializeTransformImpl((object)value, transformer);
            }
        }


        internal static byte[] SerializeImpl(string value) {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            return CompressBytes(bytes, 9, false, 1, CompressionMethod.lz4);
        }
        internal static TResult SerializeTransformImpl<TResult>(string value,
            FixedBufferTransformer<TResult> transformer) {
            var bytes = SerializeImpl(value);
            unsafe
            {
                fixed (byte* bytesPtr = &bytes[0])
                {
                    return transformer(bytes, bytes.Length, true);
                }
            }
        }

        /// <summary>
        /// Fallback with Json.NET
        /// </summary>
        internal static byte[] SerializeImplObj(object value) {
            byte[] bytes = ObjectSerializer.Serialize(value);
            return CompressBytes(bytes, 9, false, 1, CompressionMethod.lz4);
        }


        internal static TResult SerializeTransformImpl<TResult>(object value,
            FixedBufferTransformer<TResult> transformer) {
            var bytes = SerializeImplObj(value);
            unsafe
            {
                fixed (byte* bytesPtr = &bytes[0])
                {
                    return transformer(bytes, bytes.Length, true);
                }
            }
        }

        // TODO test dynamic resolution for primitives
        // or better do not rely on it and use type check
        internal static byte[] Serialize(object value) {
            if (value == null) throw new ArgumentNullException("value", "Root value cannot be null");
            dynamic v = value;
            return SerializeImpl(v);
        }

        public static byte[] Serialize<T>(T value) {
            if (value == null) throw new ArgumentNullException("value", "Root value cannot be null");
            dynamic v = value;
            return SerializeImpl(v);
        }



        //internal static object Serialize(object value, Type ty) {
        //    dynamic r = BlittableHelper.GetDefault(ty);
        //    if (!object.Equals(r, null)) {
        //        r = DeserializeImpl(srcPtr, srcSize, r);
        //    } else {
        //        MethodInfo genericMi;

        //        if (ty.IsGenericType &&
        //            ty.GetGenericTypeDefinition() == typeof(SortedMap<,>)) {

        //            var hasSaved = genericMethods.TryGetValue(ty, out genericMi);
        //            if (!hasSaved) {
        //                var genericArgs = ty.GetGenericArguments();
        //                var keyType = genericArgs[0];
        //                var valueType = genericArgs[1];
        //                var mi = typeof(Serializer).GetMethod("DecompressMapPtr",
        //                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        //                genericMi = mi.MakeGenericMethod(keyType, valueType);
        //                genericMethods[ty] = genericMi;
        //            }
        //            return genericMi.Invoke(null, new object[] { srcPtr });
        //        } else if (ty.IsArray) {
        //            var elemType = ty.GetElementType();
        //            if (elemType == typeof(byte)) {
        //                return DecompressBytes(srcPtr, srcSize);
        //            } else {
        //                var hasSaved = genericMethods.TryGetValue(ty, out genericMi);
        //                if (!hasSaved) {
        //                    var mi = typeof(Serializer).GetMethod("DecompressArrayDefault", BindingFlags.Static | BindingFlags.NonPublic);
        //                    genericMi = mi.MakeGenericMethod(elemType);
        //                    genericMethods[ty] = genericMi;
        //                }
        //                return genericMi.Invoke(null, new object[] { srcPtr });
        //            }
        //        } else if (ty == typeof(string)) {
        //            return DeserializeImpl(srcPtr, srcSize, String.Empty);
        //        } else {
        //            return DeserializeImpl(srcPtr, srcSize, ty);
        //        }
        //    }
        //    return r;
        //}


        /// <summary>
        /// Serialize value and apply a function to intermediate buffer
        /// </summary>
        internal static TResult SerializeTransform<T, TResult>(T value, FixedBufferTransformer<TResult> transformer) {
            if (value == null) throw new ArgumentNullException("value");
            dynamic v = value;
            return SerializeTransformImpl(v, transformer);
        }

        #endregion


        #region Generic Deserialization

        //internal static SortedMap<UKey, UValue> DeserializeImpl<UKey, UValue>(IntPtr srcPtr, int srcSize, SortedMap<UKey, UValue> result) {
        //    return DecompressMap<UKey, UValue>(srcPtr);
        //}

        //internal static U[] DeserializeImpl<U>(IntPtr srcPtr, int srcSize, U[] result) {
        //    return DecompressArray<U>(srcPtr);
        //}


        internal static double DeserializeImpl(IntPtr srcPtr, int srcSize, double result) {
            unsafe
            {
                if (srcSize > 0 && srcSize != 8) throw new ArgumentOutOfRangeException("Wrong src size");
                return (*(double*)srcPtr);
            }
        }

        internal static float DeserializeImpl(IntPtr srcPtr, int srcSize, float result) {
            unsafe
            {
                if (srcSize > 0 && srcSize != 8) throw new ArgumentOutOfRangeException("Wrong src size");
                return (*(float*)srcPtr);
            }
        }

        internal static decimal DeserializeImpl(IntPtr srcPtr, int srcSize, decimal result) {
            unsafe
            {
                if (srcSize > 0 && srcSize != 16) throw new ArgumentOutOfRangeException("Wrong src size");
                return (*(decimal*)srcPtr);
            }
        }

        internal static int DeserializeImpl(IntPtr srcPtr, int srcSize, int result) {
            unsafe
            {
                if (srcSize > 0 && srcSize != 4) throw new ArgumentOutOfRangeException("Wrong src size");
                return (*(int*)srcPtr);
            }
        }

        internal static uint DeserializeImpl(IntPtr srcPtr, int srcSize, uint result) {
            unsafe
            {
                if (srcSize > 0 && srcSize != 4) throw new ArgumentOutOfRangeException("Wrong src size");
                return (*(uint*)srcPtr);
            }
        }

        internal static long DeserializeImpl(IntPtr srcPtr, int srcSize, long result) {
            unsafe
            {
                if (srcSize > 0 && srcSize != 8) throw new ArgumentOutOfRangeException("Wrong src size");
                return (*(long*)srcPtr);
            }
        }

        internal static ulong DeserializeImpl(IntPtr srcPtr, int srcSize, ulong result) {
            unsafe
            {
                if (srcSize > 0 && srcSize != 8) throw new ArgumentOutOfRangeException("Wrong src size");
                return (*(ulong*)srcPtr);
            }
        }

        internal static short DeserializeImpl(IntPtr srcPtr, int srcSize, short result) {
            unsafe
            {
                if (srcSize > 0 && srcSize != 2) throw new ArgumentOutOfRangeException(nameof(srcSize), "Wrong src size");
                return (*(short*)srcPtr);
            }
        }

        internal static ushort DeserializeImpl(IntPtr srcPtr, int srcSize, ushort result) {
            unsafe
            {
                if (srcSize > 0 && srcSize != 2) throw new ArgumentOutOfRangeException("Wrong src size");
                return (*(ushort*)srcPtr);
            }
        }

        internal static byte DeserializeImpl(IntPtr srcPtr, int srcSize, byte result) {
            unsafe
            {
                if (srcSize > 0 && srcSize != 1) throw new ArgumentOutOfRangeException("Wrong src size");
                return (*(byte*)srcPtr);
            }
        }

        internal static sbyte DeserializeImpl(IntPtr srcPtr, int srcSize, sbyte result) {
            unsafe
            {
                if (srcSize > 0 && srcSize != 1) throw new ArgumentOutOfRangeException("Wrong src size");
                return (*(sbyte*)srcPtr);
            }
        }


        internal static char DeserializeImpl(IntPtr srcPtr, int srcSize, char result) {
            unsafe
            {
                if (srcSize > 0 && srcSize != 2) throw new ArgumentOutOfRangeException("Wrong src size");
                return (*(char*)srcPtr);
            }
        }

        internal static bool DeserializeImpl(IntPtr srcPtr, int srcSize, bool result) {
            unsafe
            {
                if (srcSize > 0 && srcSize != 2) throw new ArgumentOutOfRangeException("Wrong src size");
                return (*(bool*)srcPtr);
            }
        }

        internal static DateTime DeserializeImpl(IntPtr srcPtr, int srcSize, DateTime result) {
            unsafe
            {
                if (srcSize > 0 && srcSize != 8) throw new ArgumentOutOfRangeException("Wrong src size");
                var longDt = (*(long*)srcPtr);
                return DateTime.FromBinary(longDt);
            }
        }

        //internal static byte[] DeserializeImpl(IntPtr srcPtr, int srcSize, byte[] result) {
        //    return DecompressBytes(srcPtr, srcSize);
        //}

        internal static string DeserializeImpl(IntPtr srcPtr, int srcSize, string result) {
            byte[] bytes = DecompressBytes(srcPtr, srcSize);
            if (bytes == null) {
                bytes = new byte[srcSize];
                Marshal.Copy(srcPtr, bytes, 0, srcSize);
            }
            return Encoding.UTF8.GetString(bytes);
        }

        internal static object DeserializeImpl(IntPtr srcPtr, int srcSize, Type type) {
            byte[] bytes = DecompressBytes(srcPtr, srcSize);
            // TODO! WTF? must check Blosc header explicitly to find out if the source is in Blosc format
            // Do not do this magic, just fail
            Debug.Assert(bytes != null);
            //if (bytes == null) {
            //    bytes = new byte[srcSize];
            //    Marshal.Copy(srcPtr, bytes, 0, srcSize);
            //}
            return ObjectSerializer.Deserialize(bytes, type);
        }

        internal static TSrtuct DeserializeImpl<TSrtuct>(IntPtr srcPtr,
            int srcSize, TSrtuct result) where TSrtuct : struct {
            var ty = typeof(TSrtuct);
            if (!ty.IsGenericType && (ty.IsLayoutSequential || ty.IsExplicitLayout)) {
                if (srcSize > 0 && srcSize != Marshal.SizeOf(ty))
                    throw new ArgumentOutOfRangeException("Wrong src size");
                var dest = Marshal.PtrToStructure(srcPtr, ty);
                return (TSrtuct)dest;
            } else {
                return (TSrtuct)DeserializeImpl(srcPtr, srcSize, typeof(TSrtuct));
            }
        }


        private static Dictionary<Type, MethodInfo> genericMethods = new Dictionary<Type, MethodInfo>();

        internal static T Deserialize<T>(IntPtr srcPtr, int srcSize) {
            return (T)Deserialize(srcPtr, srcSize, typeof(T));
        }

        internal static object Deserialize(IntPtr srcPtr, int srcSize, Type ty) {
            dynamic r = BlittableHelper.GetDefault(ty);
            if (!object.Equals(r, null)) {
                r = DeserializeImpl(srcPtr, srcSize, r);
            } else {
                MethodInfo genericMi;

                if (ty.IsGenericType &&
                    ty.GetGenericTypeDefinition() == typeof(SortedMap<,>)) {

                    var hasSaved = genericMethods.TryGetValue(ty, out genericMi);
                    if (!hasSaved) {
                        var genericArgs = ty.GetGenericArguments();
                        var keyType = genericArgs[0];
                        var valueType = genericArgs[1];
                        var mi = typeof(Serializer).GetMethod("DecompressMapPtr",
                            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                        genericMi = mi.MakeGenericMethod(keyType, valueType);
                        genericMethods[ty] = genericMi;
                    }
                    return genericMi.Invoke(null, new object[] { srcPtr });
                } else if (ty.IsArray) {
                    var elemType = ty.GetElementType();
                    if (elemType == typeof(byte)) {
                        return DecompressBytes(srcPtr, srcSize);
                    } else {
                        var hasSaved = genericMethods.TryGetValue(ty, out genericMi);
                        if (!hasSaved) {
                            var mi = typeof(Serializer).GetMethod("DecompressArrayDefault", BindingFlags.Static | BindingFlags.NonPublic);
                            genericMi = mi.MakeGenericMethod(elemType);
                            genericMethods[ty] = genericMi;
                        }
                        return genericMi.Invoke(null, new object[] { srcPtr });
                    }
                } else if (ty == typeof(string)) {
                    return DeserializeImpl(srcPtr, srcSize, String.Empty);
                } else {
                    return DeserializeImpl(srcPtr, srcSize, ty);
                }
            }
            return r;
        }


        //internal static T Deserialize<T>(IntPtr srcPtr, int srcSize) {
        //	return (T)Deserialize(srcPtr, srcSize, typeof(T));
        //}

        /// <summary>
        /// Deserialize object
        /// </summary>
        public static T Deserialize<T>(byte[] src) {
            return (T)Deserialize(src, typeof(T));

            //if (src == null) return default(T); //throw new ArgumentNullException("src");
            ////if (src.Length == 0)
            ////{

            ////	if (typeof (T).IsArray)
            ////	{
            ////		var elTy = 
            ////		return 
            ////	}else {
            ////	return throw new ArgumentException("src is empty");
            ////}

            //unsafe
            //{
            //	fixed (byte* srcPtr = &src[0])
            //	{
            //		return Deserialize<T>((IntPtr)srcPtr, src.Length);
            //	}
            //}
        }

        /// <summary>
        /// Deserialize object
        /// </summary>
        public static object Deserialize(byte[] src, Type ty) {
            if (src == null) return null;// throw new ArgumentNullException("src");
            if (src.Length == 0) {
                if (ty.IsArray) {
                    var elTy = ty.GetElementType();
                    return Array.CreateInstance(ty.GetElementType(), 0);
                } else if (ty.IsGenericType && ty.GetGenericTypeDefinition() == typeof(SortedMap<,>)) {
                    return Activator.CreateInstance(ty);
                } else if (ty.IsGenericType && ty.GetGenericTypeDefinition() == typeof(Series<,>)) {
                    throw new NotImplementedException("TODO Call SortedMapConstructor with the same generic types, TODO tests");
                } else {
                    throw new ArgumentException("src is empty");
                }
            }
            unsafe
            {
                fixed (byte* srcPtr = &src[0])
                {
                    return Deserialize((IntPtr)srcPtr, src.Length, ty);
                }
            }
        }

        #endregion


    }


}
