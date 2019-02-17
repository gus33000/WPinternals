using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System;

namespace WPinternals
{
    public class FFUPayloadOptimization
    {
        private class HashedChunk
        {
            public UInt32 Index;
            public UInt64 Hash;
            public UInt32 StreamIndex;

            public HashedChunk(UInt32 Index, UInt64 Hash, UInt32 StreamIndex)
            {
                this.Index = Index;
                this.Hash = Hash;
                this.StreamIndex = StreamIndex;
            }
        }

        internal class FlashingPayload
        {
            public UInt32 ChunkCount;
            public byte[][] ChunkHashes;
            public UInt32[] TargetLocations;
            public UInt32[] StreamIndexes;
            public UInt32[] StreamLocations;

            public FlashingPayload(UInt32 ChunkCount, byte[][] ChunkHashes, UInt32[] TargetLocations, UInt32[] StreamIndexes, UInt32[] StreamLocations)
            {
                this.ChunkCount = ChunkCount;
                this.ChunkHashes = ChunkHashes;
                this.TargetLocations = TargetLocations;
                this.StreamIndexes = StreamIndexes;
                this.StreamLocations = StreamLocations;
            }

            public UInt32 GetSecurityHeaderSize()
            {
                return 0x20 * (UInt32)ChunkHashes.Count();
            }

            public UInt32 GetStoreHeaderSize()
            {
                return 0x08 * ((UInt32)TargetLocations.Count() + 1);
            }
        }

        //
        // Function to fall back into the legacy implementation of custom flash, to test the modifications done in the custom flash function
        // in LumiaV2UnlockBootViewModel
        //
        internal static FlashingPayload[] GetNonOptimizedPayloads(List<FlashPart> flashParts, int chunkSize)
        {
            var crypto = System.Security.Cryptography.SHA256.Create();
            List<FlashingPayload> flashingPayloads = new List<FlashingPayload>();
            if (flashParts == null)
                return flashingPayloads.ToArray();
            for (UInt32 j = 0; j < flashParts.Count; j++)
            {
                FlashPart flashPart = flashParts[(Int32)j];
                flashPart.Stream.Seek(0, SeekOrigin.Begin);
                var totalChunkCount = flashPart.Stream.Length / chunkSize;
                var br = new BinaryReader(flashPart.Stream);
                for (UInt32 i = 0; i < totalChunkCount; i++)
                {
                    byte[] buffer = new byte[chunkSize];
                    br.Read(buffer, 0, chunkSize);
                    flashingPayloads.Add(new FlashingPayload(1, new byte[][] { crypto.ComputeHash(buffer) }, new UInt32[] { (flashPart.StartSector * 0x200 / (UInt32)chunkSize) + i }, new UInt32[] { j }, new UInt32[] { i * (UInt32)chunkSize }));
                    LogFile.Log("Stream location: " + (i * (UInt32)chunkSize).ToString() + " Dest chunk: " + ((flashPart.StartSector * 0x200 / (UInt32)chunkSize) + i).ToString() + " Start chunk: " + (flashPart.StartSector * 0x200 / (UInt32)chunkSize).ToString(), LogType.FileAndConsole);
                }
            }

            return flashingPayloads.ToArray();
        }

        //
        // This function finds in an optimized way the number of duplicate chunks in a given stream, and returns
        // a list of elements, defining a chunk occurence in said stream and the chunk precomputed SHA256 hash.
        //
        internal static FlashingPayload[] GetOptimizedPayloads(List<FlashPart> flashParts, Int32 chunkSize, SetWorkingStatus SetWorkingStatus = null, UpdateWorkingStatus UpdateWorkingStatus = null)
        {
            SetWorkingStatus("Preparing resources...", "Initializing flash...", null, Status: WPinternalsStatus.Initializing);
            List<FlashingPayload> flashingPayloads = new List<FlashingPayload>();
            List<HashedChunk> hashList = new List<HashedChunk>();

            UInt64 prevhash = 0;

            // We need to do some tests here to find out which SHA256 implementation is faster, this is what takes the most time when computing the flashing payloads
            System.Security.Cryptography.SHA256 cryptographicAlgorithm = System.Security.Cryptography.SHA256.Create("System.Security.Cryptography.SHA256CryptoServiceProvider");

            long TotalProcess1 = 0;
            for (Int32 j = 0; j < flashParts.Count; j++)
            {
                FlashPart flashPart = flashParts[j];
                TotalProcess1 += flashPart.Stream.Length / chunkSize;
            }

            SetWorkingStatus("Hashing resources...", "Initializing flash...", (UInt64)TotalProcess1, Status: WPinternalsStatus.Initializing);
            long CurrentProcess1 = 0;

            byte[] buffer = new byte[chunkSize];

            // We loop through each flashpart and chunks, and index them in a list with:
            // - their flashPart index
            // - their chunk index in the stream
            // - their hash done using MurMur2 and MurMur3
            //
            for (UInt32 j = 0; j < flashParts.Count; j++)
            {
                FlashPart flashPart = flashParts[(Int32)j];
                Stream currentStream = flashPart.Stream;
                currentStream.Seek(0, SeekOrigin.Begin);

                long totalChunkCount = currentStream.Length / chunkSize;
                for (UInt32 i = 0; i < totalChunkCount; i++)
                {
                    UpdateWorkingStatus("Hashing resources...", "Initializing flash...", (UInt64)CurrentProcess1, WPinternalsStatus.Initializing);

                    currentStream.Read(buffer, 0, chunkSize);

                    // We use MurMurHash here for some key reasons:
                    // - It's built for hashtable purposes, so while it is far from secure, it's really efficient at finding out of data is the same or not
                    // - It has been rated for a 2GB/s speed (tests done in ram)
                    //
                    // However it has a collision rate, while it's low, I prefer to be safe, so I use both the third and second implementation of MurMur
                    // This way, we can be sure the data is unique, since both implementations differ, and will produce different hash algorithms.
                    //
                    UInt64 hash = MurMurHash2.Hash(buffer) + (UInt64)MurMurHash3.Hash(buffer);
                    hashList.Add(new HashedChunk(i, hash, j));

                    CurrentProcess1++;
                }
            }

            // We order resources here so we can actually efficiently group resources which are unique
            // without too much for loops, thus increasing processing time
            //
            SetWorkingStatus("Sorting resources...", "Initializing flash...", null, Status: WPinternalsStatus.Initializing);
            HashedChunk[] sortedList = hashList.OrderBy(x => x.Hash).ToArray();

            SetWorkingStatus("Identifying resources...", "Initializing flash...", (UInt64)sortedList.Count(), Status: WPinternalsStatus.Initializing);
            long CurrentProcess2 = 0;

            // Now we group resources and we compute their SHA256 hash.
            //
            foreach (HashedChunk element in sortedList)
            {
                UpdateWorkingStatus("Identifying resources...", "Initializing flash...", (UInt64)CurrentProcess2, WPinternalsStatus.Initializing);

                FlashPart flashPart = flashParts[(Int32)element.StreamIndex];
                if (element.Hash != prevhash)
                {
                    Stream currentStream = flashPart.Stream;
                    prevhash = element.Hash;

                    currentStream.Seek(element.Index * chunkSize, SeekOrigin.Begin);
                    currentStream.Read(buffer, 0, chunkSize);

                    byte[] HashValue = cryptographicAlgorithm.ComputeHash(buffer);

                    flashingPayloads.Add(new FlashingPayload(1, new byte[][] { HashValue }, new UInt32[0], new UInt32[] { element.StreamIndex }, new UInt32[] { element.Index * (UInt32)chunkSize }));
                }

                FlashingPayload flashingPayload = flashingPayloads[flashingPayloads.Count() - 1];

                List<UInt32> targetLocations = flashingPayload.TargetLocations.ToList();
                targetLocations.Add((flashPart.StartSector * 0x200 / (UInt32)chunkSize) + element.Index);
                flashingPayload.TargetLocations = targetLocations.ToArray();

                CurrentProcess2++;
            }

            //
            // Seems like even MS own FFU building suite does not support building images with multiple chunks per payload
            // Is this really supported for real?
            //
            // Comment out this code to have only one chunk per Location descriptor.
            //
            // This code deals with more than one Block for payloads
            // FlashApp seems to throw Hash Mismatch, regardless of:
            // - If we send the hash for all chunks combined
            // - If we send one hash per chunk
            // - If we send the hash for the first chunk
            //
            /*FlashingPayload[] singleChunkPayloadsList = flashingPayloads.Where(x => x.TargetLocations.Count() == 1).OrderBy(x => x.TargetLocations.First()).ToArray();
            FlashingPayload[] multipleChunkPayloadsList = flashingPayloads.Where(x => x.TargetLocations.Count() != 1).OrderBy(x => x.TargetLocations.First()).ToArray();

            List<FlashingPayload> nflashingPayloads = new List<FlashingPayload>();

            UInt32 prevlocation = 0;

            // We process each chunk that is alone
            // If it's alone and right next to another chunk being alone
            // We merge both together in a single payload, and merge the hashes
            //
            foreach (FlashingPayload element in singleChunkPayloadsList)
            {
                // Check if the chunk is consecutive to the previous one
                if (prevlocation + 1 != element.TargetLocations.First() || nflashingPayloads[nflashingPayloads.Count() - 1].ChunkCount == 16) // My phones do not support more than 16 chunks it seems
                {
                    nflashingPayloads.Add(new FlashingPayload(0, new byte[0][], element.TargetLocations, new UInt32[0], new UInt32[0]));
                }
                
                prevlocation = element.TargetLocations.First();

                FlashingPayload currPayload = nflashingPayloads[nflashingPayloads.Count() - 1];

                List<byte[]> hashlst = currPayload.ChunkHashes.ToList();
                List<UInt32> streamindexlst = currPayload.StreamIndexes.ToList();
                List<UInt32> streamlocationlst = currPayload.StreamLocations.ToList();

                hashlst.Add(element.ChunkHashes.First());
                streamindexlst.Add(element.StreamIndexes.First());
                streamlocationlst.Add(element.StreamLocations.First());

                currPayload.ChunkCount += 1;
                currPayload.ChunkHashes = hashlst.ToArray();
                currPayload.StreamIndexes = streamindexlst.ToArray();
                currPayload.StreamLocations = streamlocationlst.ToArray();
            }

            // Don't forget to add the chunks with multiple locations back
            //
            foreach (FlashingPayload payload in multipleChunkPayloadsList)
                nflashingPayloads.Add(payload);

            return nflashingPayloads.OrderBy(x => x.TargetLocations.First()).OrderBy(x => x.ChunkCount).Reverse().ToArray();

            // If you commented the above code to get single chunks, */return flashingPayloads.ToArray();
        }

        /***** BEGIN LICENSE BLOCK *****
         * Version: MPL 1.1/GPL 2.0/LGPL 2.1
         *
         * The contents of this file are subject to the Mozilla Public License Version
         * 1.1 (the "License"); you may not use this file except in compliance with
         * the License. You may obtain a copy of the License at
         * http://www.mozilla.org/MPL/
         *
         * Software distributed under the License is distributed on an "AS IS" basis,
         * WITHOUT WARRANTY OF ANY KIND, either express or implied. See the License
         * for the specific language governing rights and limitations under the
         * License.
         *
         * The Original Code is HashTableHashing.MurmurHash2.
         *
         * The Initial Developer of the Original Code is
         * Davy Landman.
         * Portions created by the Initial Developer are Copyright (C) 2009
         * the Initial Developer. All Rights Reserved.
         *
         * Contributor(s):
         *
         *
         * Alternatively, the contents of this file may be used under the terms of
         * either the GNU General Public License Version 2 or later (the "GPL"), or
         * the GNU Lesser General Public License Version 2.1 or later (the "LGPL"),
         * in which case the provisions of the GPL or the LGPL are applicable instead
         * of those above. If you wish to allow use of your version of this file only
         * under the terms of either the GPL or the LGPL, and not to allow others to
         * use your version of this file under the terms of the MPL, indicate your
         * decision by deleting the provisions above and replace them with the notice
         * and other provisions required by the GPL or the LGPL. If you do not delete
         * the provisions above, a recipient may use your version of this file under
         * the terms of any one of the MPL, the GPL or the LGPL.
         *
         * ***** END LICENSE BLOCK ***** */
        public class MurMurHash2
        {
            //Change to suit your needs
            const UInt32 seed = 0xc58f1a7b;

            [StructLayout(LayoutKind.Explicit)]
            struct bytetoUInt32Converter
            {
                [FieldOffset(0)]
                public byte[] bytes;

                [FieldOffset(0)]
                public UInt32[] UInt32s;
            }

            public static UInt32 Hash(byte[] data)
            {
                const UInt32 m = 0x5bd1e995;
                const Int32 r = 24;

                Int32 length = data.Length;
                if (length == 0)
                    return 0;
                UInt32 h = seed ^ (UInt32)length;
                Int32 currentIndex = 0;
                // array will be length of bytes but contains UInt32s
                // therefore the currentIndex will jump with +1 while length will jump with +4
                UInt32[] hackArray = new bytetoUInt32Converter { bytes = data }.UInt32s;
                while (length >= 4)
                {
                    UInt32 k = hackArray[currentIndex++];
                    k *= m;
                    k ^= k >> r;
                    k *= m;

                    h *= m;
                    h ^= k;
                    length -= 4;
                }
                currentIndex *= 4; // fix the length
                switch (length)
                {
                    case 3:
                        h ^= (UInt16)(data[currentIndex++] | data[currentIndex++] << 8);
                        h ^= (UInt32)data[currentIndex] << 16;
                        h *= m;
                        break;
                    case 2:
                        h ^= (UInt16)(data[currentIndex++] | data[currentIndex] << 8);
                        h *= m;
                        break;
                    case 1:
                        h ^= data[currentIndex];
                        h *= m;
                        break;
                    default:
                        break;
                }

                // Do a few final mixes of the hash to ensure the last few
                // bytes are well-incorporated.

                h ^= h >> 13;
                h *= m;
                h ^= h >> 15;

                return h;
            }
        }

        /*
        This code is public domain.

        The MurmurHash3 algorithm was created by Austin Appleby and put into the public domain.  See http://code.google.com/p/smhasher/

        This C# variant was authored by
        Elliott B. Edwards and was placed into the public domain as a gist
        Status...Working on verification (Test Suite)
        Set up to run as a LinqPad (linqpad.net) script (thus the ".Dump()" call)
        */
        public static class MurMurHash3
        {
            //Change to suit your needs
            const UInt32 seed = 144;

            public static UInt32 Hash(byte[] data)
            {
                return Hash(new MemoryStream(data));
            }

            public static UInt32 Hash(Stream stream)
            {
                const UInt32 c1 = 0xcc9e2d51;
                const UInt32 c2 = 0x1b873593;

                UInt32 h1 = seed;
                UInt32 k1 = 0;
                UInt32 streamLength = 0;

                using (BinaryReader reader = new BinaryReader(stream))
                {
                    byte[] chunk = reader.ReadBytes(4);
                    while (chunk.Length > 0)
                    {
                        streamLength += (UInt32)chunk.Length;
                        switch (chunk.Length)
                        {
                            case 4:
                                /* Get four bytes from the input into an UInt32 */
                                k1 = (UInt32)
                                   (chunk[0]
                                  | chunk[1] << 8
                                  | chunk[2] << 16
                                  | chunk[3] << 24);

                                /* bitmagic hash */
                                k1 *= c1;
                                k1 = rotl32(k1, 15);
                                k1 *= c2;

                                h1 ^= k1;
                                h1 = rotl32(h1, 13);
                                h1 = h1 * 5 + 0xe6546b64;
                                break;
                            case 3:
                                k1 = (UInt32)
                                   (chunk[0]
                                  | chunk[1] << 8
                                  | chunk[2] << 16);
                                k1 *= c1;
                                k1 = rotl32(k1, 15);
                                k1 *= c2;
                                h1 ^= k1;
                                break;
                            case 2:
                                k1 = (UInt32)
                                   (chunk[0]
                                  | chunk[1] << 8);
                                k1 *= c1;
                                k1 = rotl32(k1, 15);
                                k1 *= c2;
                                h1 ^= k1;
                                break;
                            case 1:
                                k1 = (UInt32)(chunk[0]);
                                k1 *= c1;
                                k1 = rotl32(k1, 15);
                                k1 *= c2;
                                h1 ^= k1;
                                break;

                        }
                        chunk = reader.ReadBytes(4);
                    }
                }

                // finalization, magic chants to wrap it all up
                h1 ^= streamLength;
                h1 = fmix(h1);

                unchecked //ignore overflow
                {
                    return h1;
                }
            }

            private static UInt32 rotl32(UInt32 x, byte r)
            {
                return (x << r) | (x >> (32 - r));
            }

            private static UInt32 fmix(UInt32 h)
            {
                h ^= h >> 16;
                h *= 0x85ebca6b;
                h ^= h >> 13;
                h *= 0xc2b2ae35;
                h ^= h >> 16;
                return h;
            }
        }
    }
}
