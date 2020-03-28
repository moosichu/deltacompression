using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Burst;
using K4os.Compression.LZ4;

namespace TMRC.DeltaCompression
{

    public struct CompressedByte
    {
#pragma warning disable CS0649
        internal byte Data;
#pragma warning restore CS0649
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
            public NativeArray<CompressedByte> DestinationData;
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

        public unsafe static NativeSlice<byte> GetBytes(this NativeArray<CompressedByte> compressedData)
        {
            UnsafeUtility.CopyPtrToStructure(NativeArrayUnsafeUtility.GetUnsafePtr(compressedData), out int length);
            return new NativeSlice<byte>(
                compressedData.Reinterpret<byte>(),
                UnsafeUtility.SizeOf<int>(),
                length
            );
        }

        public unsafe static int GetNumBytes(this NativeArray<CompressedByte> compressedData)
        {
            UnsafeUtility.CopyPtrToStructure(NativeArrayUnsafeUtility.GetUnsafePtr(compressedData), out int length);
            return length;
        }

        public unsafe static byte* GetBytes(this NativeArray<CompressedByte> compressedData, out int length)
        {
            UnsafeUtility.CopyPtrToStructure(NativeArrayUnsafeUtility.GetUnsafePtr(compressedData), out length);
            return ((byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(compressedData)) + UnsafeUtility.SizeOf<int>();
        }

        public static int MaximumDeltaCompressionSize(int Length)
        {
            return UnsafeUtility.SizeOf<int>() + LZ4Codec.MaximumOutputSize(Length);
        }

        public static JobHandle DeltaCompress(
            NativeSlice<byte> inData0,
            NativeSlice<byte> inOutData1, // Will be modified in-place
            NativeArray<CompressedByte> outCompressedDelta1,
            LZ4Level compressionLevel,
            JobHandle jobHandle
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
                DestinationData = outCompressedDelta1,
                CompressionLevel = compressionLevel,
            }.Schedule(jobHandle);

            return jobHandle;
        }

        public static JobHandle DeltaDeCompress
        (
            NativeSlice<byte> inData0,
            NativeSlice<byte> inCompressedDelta1,
            NativeSlice<byte> outData1,
            JobHandle jobHandle
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
