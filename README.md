# Delta Compression in Unity

This is a [Unity Package](https://docs.unity3d.com/Manual/PackagesList.html) designed for Unity that allows you to perform binary delta compression given two similar native arrays of bytes.

This package depends on slightly modified versions of the [K4os.Compression.LZ4](https://github.com/moosichu/K4os.Compression.LZ4) ([original](https://github.com/MiloszKrajewski/K4os.Compression.LZ4)) library. Instruction on how to setup this package with the correct dependencies can be found below.

This library as has a couple of helper functions for standard LZ4 compression as well.

## Setup

Add the following dependencies to your Unity project's `manifest.json` file:

```
"com.moosichu.deltacompression": "https://github.com/moosichu/taggedunions.git",
"k4os.compression.lz4": "https://github.com/moosichu/K4os.Compression.LZ4.git#moosichu/unity-support",
"k4os.hash.xxhash": "https://github.com/moosichu/K4os.Hash.xxHash.git#moosichu/unity-support",
```

This will import the package*, along with the dependencies.

*It's worth noting that you should create your own copy of this repository and reference that as a package if you are using it in production, in-case this repo isn't available to you for whichever reason.

## Usage

Below is the code for a sample static function that fills two arrays with random byte and then adds a few random bytes at random intervals to the second array. The second array is then compressed, we print out the compressed size and compression factor, and then decompress it and assert that we have constructed the original array.

As this is a simple library this should be enough to start with, if not the source code has some comments as well.

```CS
static void CompressTest()
{
    NativeArray<byte> bytes1 = new NativeArray<byte>(1000_000, Allocator.TempJob);
    NativeArray<byte> bytes2 = new NativeArray<byte>(1000_000, Allocator.TempJob);
    NativeArray<byte> bytes2Copy = new NativeArray<byte>(1000_000, Allocator.TempJob);
    int maximumLength = DeltaCompression.MaximumDeltaCompressionSize(bytes1.Length);
    Debug.Log($"We are compressing {bytes2.Length}, the maximum compressed size it could be is {maximumLength}");

    // CompressedBytesStorage allocates a backing array which will always be big enough to store the
    // the compressed result. After compression has completed you can extract the compressed data with
    // GetBytes()
    CompressedBytesStorage compressedData = new CompressedBytesStorage(bytes1.Length, Allocator.TempJob);
    try
    {
        // Fill a byte array with random numbers
        Unity.Mathematics.Random random = new Unity.Mathematics.Random(1);
        for (int i = 0; i < bytes1.Length; i++)
        {
            bytes1[i] = unchecked((byte)random.NextInt());
        }

        // Copy this to a second array
        bytes1.CopyTo(bytes2);

        // Randomise some bytes in the second array to ensure it does have some
        // differences
        for (int i = 0; i < bytes2.Length; i++)
        {
            bytes2[i] = unchecked((byte)random.NextInt());
            i += (int)(random.NextUInt() % 1000);
        }

        // We create a copy of the byte array we are going to compress, as the
        // compression function will modify the originally slightly in-place as
        // it runs.
        bytes2.CopyTo(bytes2Copy);

        {
            int numBytesDifferent = 0;
            for (int i = 0; i < bytes2.Length; i++)
            {
                if(bytes1[i] != bytes2[i])
                {
                    numBytesDifferent++;
                }
            }
            Debug.Log($"The number of bytes which differ is {numBytesDifferent}");
        }

        // Compress the second byte array
        JobHandle jobHandle = DeltaCompression.DeltaCompress(bytes1, bytes2, compressedData, default);
        jobHandle.Complete();

        NativeSlice<byte> compressedBytes = compressedData.GetBytes();
        Debug.Log($"The compressed size is {compressedBytes.Length}, this is a compression factor of {(float) compressedBytes.Length / (float) bytes2.Length}");

        // Fill the second byte array with random data, just so we know that we will be reconstructing it properly.
        for (int i = 0; i < bytes2.Length; i++)
        {
            bytes2[i] = unchecked((byte)random.NextInt());
        }

        jobHandle = DeltaCompression.DeltaDeCompress(bytes1, compressedBytes, bytes2, jobHandle);
        jobHandle.Complete();

        // Assert decompression works.
        for (int i = 0; i < bytes2.Length; i++)
        {
            Debug.Assert(bytes2[i] == bytes2Copy[i]);
        }

        Debug.Log("Compression Success!");
    }
    finally
    {
        compressedData.Dispose();
        bytes2Copy.Dispose();
        bytes2.Dispose();
        bytes1.Dispose();
    }
}
```


