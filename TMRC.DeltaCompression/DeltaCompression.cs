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
            [ReadOnly] public NativeArray<byte> Data0;
            public NativeArray<byte> InPlaceData1;

            public void Execute(int index)
            {
                InPlaceData1[index] = (byte)(InPlaceData1[index] ^ Data0[index]);
            }
        }

       // We don't compile this with burst as it doesn't work :(
        private struct EncodeJob : IJob
        {
            [ReadOnly] public NativeArray<byte> SourceData;
            public NativeArray<CompressedByte> DestinationData;
            public LZ4Level CompressionLevel;

            public unsafe void Execute()
            {
                Debug.Assert(DestinationData.Length >= MaximumDeltaCompressionSize(SourceData.Length));
                int compressionSize = LZ4Codec.Encode(
                    (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(SourceData),
                    SourceData.Length,
                    ((byte*)NativeArrayUnsafeUtility.GetUnsafePtr(DestinationData)) + UnsafeUtility.SizeOf<int>(),
                    DestinationData.Length
                );
                // We store the length in the first part of the native array we write to
                UnsafeUtility.CopyStructureToPtr(ref compressionSize, NativeArrayUnsafeUtility.GetUnsafePtr(DestinationData));
            }
        }

        public unsafe static NativeSlice<byte> GetBytes(this NativeArray<CompressedByte> compressedData)
        {
            UnsafeUtility.CopyPtrToStructure(NativeArrayUnsafeUtility.GetUnsafePtr(compressedData), out int Length);
            return new NativeSlice<byte>(
                compressedData.Reinterpret<byte>(),
                UnsafeUtility.SizeOf<int>(),
                Length
            );
        }

        public static int MaximumDeltaCompressionSize(int Length)
        {
            return UnsafeUtility.SizeOf<int>() + LZ4Codec.MaximumOutputSize(Length);
        }

        public static JobHandle DeltaCompress(
            NativeArray<byte> inData0,
            NativeArray<byte> inOutData1, // Will be modified in-place
            NativeArray<CompressedByte> outCompressedDelta1,
            LZ4Level compressionLevel,
            JobHandle jobHandle
        )
        {
            Debug.Assert(inData0.Length == inOutData1.Length);
            jobHandle = new XorJob()
            {
                Data0 = inData0,
                InPlaceData1 = inOutData1,
            }.Schedule(inData0.Length, 64, jobHandle);
            // XOR the data so we can easily run-length encode the changed bits

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
            NativeArray<byte> inData0,
            NativeSlice<byte> inCompressedDelta1,
            NativeArray<byte> outData1,
            JobHandle jobHandle
        )
        {

            jobHandle = new XorJob()
            {
                Data0 = inData0,
                InPlaceData1 = outData1,
            }.Schedule(inData0.Length, 64, jobHandle);
            return jobHandle;
        }

    }

}
