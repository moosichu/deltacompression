using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Burst;
using K4os.Compression.LZ4;
using System;

namespace TMRC.DeltaCompression
{

    /// <summary>
    /// An opaque handle that DeltaCompression.DeltaCompress() uses as a data
    /// allocation destination.
    ///
    /// You can initialise it by providing the length of the source data that
    /// this will store as input. You can call
    /// DeltaCompression.MaximumDeltaCompressionSize() to workout what the size
    /// of the array this structure allocates will be.
    ///
    /// Once you have run DeltaCompression.DeltaCompress(), you can extract the bytes
    /// that have been produced by compression by calling GetBytes on the
    /// CompressedBytesStorage instance.
    /// </summary>
    public struct CompressedBytesStorage : IDisposable
    {
        public int Length => Data.Length;
        internal NativeArray<byte> Data;
        public void Dispose()
        {
            Data.Dispose();
        }

        public CompressedBytesStorage(int sourceDataLength, Allocator allocator)
        {
            Data = new NativeArray<byte>(DeltaCompression.MaximumDeltaCompressionSize(sourceDataLength), allocator);
        }

        public unsafe NativeSlice<byte> GetBytes()
        {
            UnsafeUtility.CopyPtrToStructure(NativeArrayUnsafeUtility.GetUnsafePtr(Data), out int length);
            return new NativeSlice<byte>(
                Data,
                UnsafeUtility.SizeOf<int>(),
                length
            );
        }

        public unsafe int GetNumBytes()
        {
            UnsafeUtility.CopyPtrToStructure(NativeArrayUnsafeUtility.GetUnsafePtr(Data), out int length);
            return length;
        }

        public unsafe byte* GetBytes(out int length)
        {
            UnsafeUtility.CopyPtrToStructure(NativeArrayUnsafeUtility.GetUnsafePtr(Data), out length);
            return ((byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(Data)) + UnsafeUtility.SizeOf<int>();
        }
    }

    public static class DeltaCompression
    {

        [BurstCompile]
        private struct XorJob : IJobParallelFor
        {
            [ReadOnly] public NativeSlice<byte> Data0;
            public NativeSlice<byte> InPlaceData1;

            public void Execute(int index)
            {
                InPlaceData1[index] = (byte)(InPlaceData1[index] ^ Data0[index]);
            }
        }

        // We don't compile this with burst as it doesn't work :NoBurstLZ4
        private struct EncodeJob : IJob
        {
            [ReadOnly] public NativeSlice<byte> SourceData;
            public NativeArray<byte> DestinationData;
            public LZ4Level CompressionLevel;

            public unsafe void Execute()
            {
                Debug.Assert(DestinationData.Length >= MaximumDeltaCompressionSize(SourceData.Length));
                int compressionSize = LZ4Codec.Encode(
                    (byte*)NativeSliceUnsafeUtility.GetUnsafeReadOnlyPtr(SourceData),
                    SourceData.Length,
                    ((byte*)NativeArrayUnsafeUtility.GetUnsafePtr(DestinationData)) + UnsafeUtility.SizeOf<int>(),
                    DestinationData.Length,
                    CompressionLevel
                );
                // We store the length in the first part of the native array we write to
                UnsafeUtility.CopyStructureToPtr(ref compressionSize, NativeArrayUnsafeUtility.GetUnsafePtr(DestinationData));
            }
        }

        // We don't compile this with burst as it doesn't work :NoBurstLZ4
        private struct DecodeJob : IJob
        {
            public NativeSlice<byte> SourceData;
            public NativeSlice<byte> DestinationData;

            public unsafe void Execute()
            {
                int decodedSize = LZ4Codec.Decode(
                    (byte*)NativeSliceUnsafeUtility.GetUnsafeReadOnlyPtr(SourceData),
                    SourceData.Length,
                    ((byte*)NativeSliceUnsafeUtility.GetUnsafePtr(DestinationData)),
                    DestinationData.Length
                );
                Debug.Assert(decodedSize == DestinationData.Length);
            }
        }

        /// <summary>
        /// Get the size of the NativeArray that will back CompressedBytesStorage
        /// when you initialise it with the source data length that you wish to
        /// compress.
        /// </summary>
        public static int MaximumDeltaCompressionSize(int Length)
        {
            return UnsafeUtility.SizeOf<int>() + LZ4Codec.MaximumOutputSize(Length);
        }

        /// <summary>
        /// Compress an array using LZ4 Compression in a Job.
        /// </summary>
        public static JobHandle StandardCompress(
            NativeSlice<byte> inData,
            CompressedBytesStorage outCompressedData,
            JobHandle jobHandle = default,
            LZ4Level compressionLevel = LZ4Level.L00_FAST
        )
        {
            jobHandle = new EncodeJob()
            {
                SourceData = inData,
                DestinationData = outCompressedData.Data,
                CompressionLevel = compressionLevel,
            }.Schedule(jobHandle);
            return jobHandle;
        }

        /// <summary>
        /// Decompress an array using LZ4 Compression in a Job.
        ///
        /// IMPORTANT NOTE: There are no safety checks to ensure that the data
        /// being fed in is valid and of the correct lengths! Please ensure that
        /// yourself.
        /// </summary>
        public static JobHandle StandardDecompress
        (
            NativeSlice<byte> inCompressedData,
            NativeSlice<byte> outData,
            JobHandle jobHandle = default
        )
        {
            jobHandle = new DecodeJob()
            {
                SourceData = inCompressedData,
                DestinationData = outData
            }.Schedule(jobHandle);
            return jobHandle;
        }

        /// <summary>
        /// Given two arrays of data (inData0, inOutData1) with minimal
        /// differences, this function will schedule jobs to compress inOutData1
        /// based on the differences.
        ///
        /// You need to provide a pre-allocated CompressedBytesStorage instance.
        ///
        /// IMPORTANT NOTE: inOutData1 is modified in-place during the
        /// the compression process. So please make a copy of it if you wish
        /// to preserve it's state pre-compression.
        ///
        /// Once these jobs have finished running, you can call GetBytes on
        /// the CompressedBytesStorage instance to get the compressed data and
        /// its final size.
        ///
        /// inData0 will be needed to reproduce inOutData1 from the compressed
        /// delta.
        ///
        /// Implementation Details:
        /// We xor the two input arrays (storing the output in the second one)
        /// in order to generate a byte array of the difference to which we
        /// then apply LZ4 compression at the specified level.
        /// </summary>
        public static JobHandle DeltaCompress(
            NativeSlice<byte> inData0,
            NativeSlice<byte> inOutData1, // Will be modified in-place
            CompressedBytesStorage outCompressedDelta1,
            JobHandle jobHandle = default,
            LZ4Level compressionLevel = LZ4Level.L00_FAST
        )
        {
            Debug.Assert(inData0.Length == inOutData1.Length);
            // XOR the data so we can easily run-length encode the changed bits
            jobHandle = new XorJob()
            {
                Data0 = inData0,
                InPlaceData1 = inOutData1,
            }.Schedule(inData0.Length, 64, jobHandle);

            jobHandle = new EncodeJob()
            {
                SourceData = inOutData1,
                DestinationData = outCompressedDelta1.Data,
                CompressionLevel = compressionLevel,
            }.Schedule(jobHandle);

            return jobHandle;
        }

        /// <summary>
        /// Given a source array, and the delta-compressed array of the output,
        /// reconstruct the array that slightly differs from the source array.
        ///
        /// IMPORTANT NOTE: There are no safety checks to ensure that the data
        /// being fed in is valid and of the correct lengths! Please ensure that
        /// yourself.
        /// </summary>
        public static JobHandle DeltaDecompress
        (
            NativeSlice<byte> inData0,
            NativeSlice<byte> inCompressedDelta1,
            NativeSlice<byte> outData1,
            JobHandle jobHandle = default
        )
        {
            jobHandle = new DecodeJob()
            {
                SourceData = inCompressedDelta1,
                DestinationData = outData1
            }.Schedule(jobHandle);
            jobHandle = new XorJob()
            {
                Data0 = inData0,
                InPlaceData1 = outData1,
            }.Schedule(inData0.Length, 64, jobHandle);
            return jobHandle;
        }
    }
}
