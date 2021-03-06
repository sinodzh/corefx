// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Reflection.Internal;
using System.Reflection.Metadata;
using System.Text;
using System.Threading;

namespace System.Reflection.PortableExecutable
{
    /// <summary>
    /// Portable Executable format reader.
    /// </summary>
    /// <remarks>
    /// The implementation is thread-safe, that is multiple threads can read data from the reader in parallel.
    /// Disposal of the reader is not thread-safe (see <see cref="Dispose"/>).
    /// </remarks>
    public sealed class PEReader : IDisposable
    {
        // May be null in the event that the entire image is not
        // deemed necessary and we have been instructed to read
        // the image contents without being lazy.
        private MemoryBlockProvider peImage;

        // If we read the data from the image lazily (peImage != null) we defer reading the PE headers.
        private PEHeaders lazyPEHeaders;

        private AbstractMemoryBlock lazyMetadataBlock;
        private AbstractMemoryBlock lazyImageBlock;
        private AbstractMemoryBlock[] lazyPESectionBlocks;

        /// <summary>
        /// Creates a Portable Executable reader over a PE image stored in memory.
        /// </summary>
        /// <param name="peImage">Pointer to the start of the PE image.</param>
        /// <param name="size">The size of the PE image.</param>
        /// <exception cref="ArgumentNullException"><paramref name="peImage"/> is <see cref="IntPtr.Zero"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is negative.</exception>
        /// <remarks>
        /// The memory is owned by the caller and not released on disposal of the <see cref="PEReader"/>.
        /// The caller is responsible for keeping the memory alive and unmodified throughout the lifetime of the <see cref="PEReader"/>.
        /// The content of the image is not read during the construction of the <see cref="PEReader"/>
        /// </remarks>
        public unsafe PEReader(byte* peImage, int size)
        {
            if (peImage == null)
            {
                throw new ArgumentNullException("peImage");
            }

            if (size < 0)
            {
                throw new ArgumentOutOfRangeException("size");
            }

            this.peImage = new ExternalMemoryBlockProvider(peImage, size);
        }

        /// <summary>
        /// Creates a Portable Executable reader over a PE image stored in a stream.
        /// </summary>
        /// <param name="peStream">PE image stream.</param>
        /// <exception cref="ArgumentNullException"><paramref name="peStream"/> is null.</exception>
        /// <exception cref="BadImageFormatException">
        /// <see cref="PEStreamOptions.PrefetchMetadata"/> is specified and the PE headers of the image are invalid.
        /// </exception>
        /// <remarks>
        /// Ownership of the stream is transferred to the <see cref="PEReader"/> upon successful validation of constructor arguments. It will be 
        /// disposed by the <see cref="PEReader"/> and the caller must not manipulate it.
        /// </remarks>
        public PEReader(Stream peStream)
            : this(peStream, PEStreamOptions.Default)
        {
        }

        /// <summary>
        /// Creates a Portable Executable reader over a PE image stored in a stream beginning at its current position and ending at the end of the stream.
        /// </summary>
        /// <param name="peStream">PE image stream.</param>
        /// <param name="options">
        /// Options specifying how sections of the PE image are read from the stream.
        /// 
        /// Unless <see cref="PEStreamOptions.LeaveOpen"/> is specified, ownership of the stream is transferred to the <see cref="PEReader"/> 
        /// upon successful argument validation. It will be disposed by the <see cref="PEReader"/> and the caller must not manipulate it.
        /// 
        /// Unless <see cref="PEStreamOptions.PrefetchMetadata"/> or <see cref="PEStreamOptions.PrefetchEntireImage"/> is specified no data 
        /// is read from the stream during the construction of the <see cref="PEReader"/>. Furthermore, the stream must not be manipulated
        /// by caller while the <see cref="PEReader"/> is alive and undisposed.
        /// 
        /// If <see cref="PEStreamOptions.PrefetchMetadata"/> or <see cref="PEStreamOptions.PrefetchEntireImage"/>, the <see cref="PEReader"/> 
        /// will have read all of the data requested during construction. As such, if <see cref="PEStreamOptions.LeaveOpen"/> is also
        /// specified, the caller retains full ownership of the stream and is assured that it will not be manipulated by the <see cref="PEReader"/>
        /// after construction.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="peStream"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> has an invalid value.</exception>
        /// <exception cref="BadImageFormatException">
        /// <see cref="PEStreamOptions.PrefetchMetadata"/> is specified and the PE headers of the image are invalid.
        /// </exception>
        public PEReader(Stream peStream, PEStreamOptions options)
            : this(peStream, options, (int?)null)
        {
        }

        /// <summary>
        /// Creates a Portable Executable reader over a PE image of the given size beginning at the stream's current position.
        /// </summary>
        /// <param name="peStream">PE image stream.</param>
        /// <param name="size">PE image size.</param>
        /// <param name="options">
        /// Options specifying how sections of the PE image are read from the stream.
        /// 
        /// Unless <see cref="PEStreamOptions.LeaveOpen"/> is specified, ownership of the stream is transferred to the <see cref="PEReader"/> 
        /// upon successful argument validation. It will be disposed by the <see cref="PEReader"/> and the caller must not manipulate it.
        /// 
        /// Unless <see cref="PEStreamOptions.PrefetchMetadata"/> or <see cref="PEStreamOptions.PrefetchEntireImage"/> is specified no data 
        /// is read from the stream during the construction of the <see cref="PEReader"/>. Furthermore, the stream must not be manipulated
        /// by caller while the <see cref="PEReader"/> is alive and undisposed.
        /// 
        /// If <see cref="PEStreamOptions.PrefetchMetadata"/> or <see cref="PEStreamOptions.PrefetchEntireImage"/>, the <see cref="PEReader"/> 
        /// will have read all of the data requested during construction. As such, if <see cref="PEStreamOptions.LeaveOpen"/> is also
        /// specified, the caller retains full ownership of the stream and is assured that it will not be manipulated by the <see cref="PEReader"/>
        /// after construction.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">Size is negative or extends past the end of the stream.</exception>
        public PEReader(Stream peStream, PEStreamOptions options, int size)
            : this(peStream, options, (int?)size)

        {
        }

        private unsafe PEReader(Stream peStream, PEStreamOptions options, int? sizeOpt)
        {
            if (peStream == null)
            {
                throw new ArgumentNullException("peStream");
            }

            if (!peStream.CanRead || !peStream.CanSeek)
            {
                throw new ArgumentException(MetadataResources.StreamMustSupportReadAndSeek, "peStream");
            }

            if (!options.IsValid())
            {
                throw new ArgumentOutOfRangeException("options");
            }

            long start = peStream.Position;
            int size = PEBinaryReader.GetAndValidateSize(peStream, sizeOpt);

            bool closeStream = true;
            try
            {
                bool isFileStream = FileStreamReadLightUp.IsFileStream(peStream);

                if ((options & (PEStreamOptions.PrefetchMetadata | PEStreamOptions.PrefetchEntireImage)) == 0)
                {
                    this.peImage = new StreamMemoryBlockProvider(peStream, start, size, isFileStream, (options & PEStreamOptions.LeaveOpen) != 0);
                    closeStream = false;
                }
                else
                {
                    // Read in the entire image or metadata blob:
                    if ((options & PEStreamOptions.PrefetchEntireImage) != 0)
                    {
                        var imageBlock = StreamMemoryBlockProvider.ReadMemoryBlockNoLock(peStream, isFileStream, 0, (int)Math.Min(peStream.Length, int.MaxValue));
                        this.lazyImageBlock = imageBlock;
                        this.peImage = new ExternalMemoryBlockProvider(imageBlock.Pointer, imageBlock.Size);

                        // if the caller asked for metadata initialize the PE headers (calculates metadata offset):
                        if ((options & PEStreamOptions.PrefetchMetadata) != 0)
                        {
                            InitializePEHeaders();
                        }
                    }
                    else
                    {
                        // The peImage is left null, but the lazyMetadataBlock is initialized up front.
                        this.lazyPEHeaders = new PEHeaders(peStream);
                        this.lazyMetadataBlock = StreamMemoryBlockProvider.ReadMemoryBlockNoLock(peStream, isFileStream, lazyPEHeaders.MetadataStartOffset, lazyPEHeaders.MetadataSize);
                    }
                    // We read all we need, the stream is going to be closed.
                }
            }
            finally
            {
                if (closeStream && (options & PEStreamOptions.LeaveOpen) == 0)
                {
                    peStream.Dispose();
                }
            }
        }

        /// <summary>
        /// Creates a Portable Executable reader over a PE image stored in a byte array.
        /// </summary>
        /// <param name="peImage">PE image.</param>
        /// <remarks>
        /// The content of the image is not read during the construction of the <see cref="PEReader"/>
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="peImage"/> is null.</exception>
        public PEReader(ImmutableArray<byte> peImage)
        {
            if (peImage.IsDefault)
            {
                throw new ArgumentNullException("peImage");
            }

            this.peImage = new ByteArrayMemoryProvider(peImage);
        }

        /// <summary>
        /// Disposes all memory allocated by the reader.
        /// </summary>
        /// <remarks>
        /// <see cref="Dispose"/>  can be called multiple times (even in parallel). 
        /// However, it is not safe to call <see cref="Dispose"/> in parallel with any other operation on the <see cref="PEReader"/>
        /// or reading from <see cref="PEMemoryBlock"/>s retrieved from the reader.
        /// </remarks>
        public void Dispose()
        {
            var image = peImage;
            if (image != null)
            {
                image.Dispose();
                peImage = null;
            }

            var imageBlock = lazyImageBlock;
            if (imageBlock != null)
            {
                imageBlock.Dispose();
                lazyImageBlock = null;
            }

            var metadataBlock = lazyMetadataBlock;
            if (metadataBlock != null)
            {
                metadataBlock.Dispose();
                lazyMetadataBlock = null;
            }

            var peSectionBlocks = lazyPESectionBlocks;
            if (peSectionBlocks != null)
            {
                foreach (var block in peSectionBlocks)
                {
                    if (block != null)
                    {
                        block.Dispose();
                    }
                }

                lazyPESectionBlocks = null;
            }
        }

        /// <summary>
        /// Gets the PE headers.
        /// </summary>
        /// <exception cref="BadImageFormatException">The headers contain invalid data.</exception>
        public PEHeaders PEHeaders
        {
            get
            {
                if (lazyPEHeaders == null)
                {
                    InitializePEHeaders();
                }

                return lazyPEHeaders;
            }
        }

        private void InitializePEHeaders()
        {
            Debug.Assert(peImage != null);

            StreamConstraints constraints;
            Stream stream = peImage.GetStream(out constraints);

            PEHeaders headers;
            if (constraints.GuardOpt != null)
            {
                lock (constraints.GuardOpt)
                {
                    headers = ReadPEHeadersNoLock(stream, constraints.ImageStart, constraints.ImageSize);
                }
            }
            else
            {
                headers = ReadPEHeadersNoLock(stream, constraints.ImageStart, constraints.ImageSize);
            }

            Interlocked.CompareExchange(ref lazyPEHeaders, headers, null);
        }

        private static PEHeaders ReadPEHeadersNoLock(Stream stream, long imageStartPosition, int imageSize)
        {
            Debug.Assert(imageStartPosition >= 0 && imageStartPosition <= stream.Length);
            stream.Seek(imageStartPosition, SeekOrigin.Begin);
            return new PEHeaders(stream, imageSize);
        }

        /// <summary>
        /// Returns a view of the entire image as a pointer and length.
        /// </summary>
        /// <exception cref="InvalidOperationException">PE image not available.</exception>
        private AbstractMemoryBlock GetEntireImageBlock()
        {
            if (lazyImageBlock == null)
            {
                if (peImage == null)
                {
                    throw new InvalidOperationException(MetadataResources.PEImageNotAvailable);
                }

                var newBlock = peImage.GetMemoryBlock();
                if (Interlocked.CompareExchange(ref lazyImageBlock, newBlock, null) != null)
                {
                    // another thread created the block already, we need to dispose ours:
                    newBlock.Dispose();
                }
            }

            return lazyImageBlock;
        }

        private AbstractMemoryBlock GetMetadataBlock()
        {
            if (!HasMetadata)
            {
                throw new InvalidOperationException(MetadataResources.PEImageDoesNotHaveMetadata);
            }

            if (lazyMetadataBlock == null)
            {
                Debug.Assert(peImage != null, "We always have metadata if peImage is not available.");

                var newBlock = peImage.GetMemoryBlock(PEHeaders.MetadataStartOffset, PEHeaders.MetadataSize);
                if (Interlocked.CompareExchange(ref lazyMetadataBlock, newBlock, null) != null)
                {
                    // another thread created the block already, we need to dispose ours:
                    newBlock.Dispose();
                }
            }

            return lazyMetadataBlock;
        }

        private AbstractMemoryBlock GetPESectionBlock(int index)
        {
            Debug.Assert(index >= 0 && index < PEHeaders.SectionHeaders.Length);
            Debug.Assert(peImage != null);

            if (lazyPESectionBlocks == null)
            {
                Interlocked.CompareExchange(ref lazyPESectionBlocks, new AbstractMemoryBlock[PEHeaders.SectionHeaders.Length], null);
            }

            var newBlock = peImage.GetMemoryBlock(
                PEHeaders.SectionHeaders[index].PointerToRawData,
                PEHeaders.SectionHeaders[index].SizeOfRawData);

            if (Interlocked.CompareExchange(ref lazyPESectionBlocks[index], newBlock, null) != null)
            {
                // another thread created the block already, we need to dispose ours:
                newBlock.Dispose();
            }

            return lazyPESectionBlocks[index];
        }

        /// <summary>
        /// Return true if the reader can access the entire PE image.
        /// </summary>
        /// <remarks>
        /// Returns false if the <see cref="PEReader"/> is constructed from a stream and only part of it is prefetched into memory.
        /// </remarks>
        public bool IsEntireImageAvailable
        {
            get { return lazyImageBlock != null || peImage != null; }
        }

        /// <summary>
        /// Gets a pointer to and size of the PE image if available (<see cref="IsEntireImageAvailable"/>).
        /// </summary>
        /// <exception cref="InvalidOperationException">The entire PE image is not available.</exception>
        public PEMemoryBlock GetEntireImage()
        {
            return new PEMemoryBlock(GetEntireImageBlock());
        }

        /// <summary>
        /// Returns true if the PE image contains CLI metadata.
        /// </summary>
        /// <exception cref="BadImageFormatException">The PE headers contain invalid data.</exception>
        public bool HasMetadata
        {
            get { return PEHeaders.MetadataSize > 0; }
        }

        /// <summary>
        /// Loads PE section that contains CLI metadata.
        /// </summary>
        /// <exception cref="InvalidOperationException">The PE image doesn't contain metadata (<see cref="HasMetadata"/> returns false).</exception>
        /// <exception cref="BadImageFormatException">The PE headers contain invalid data.</exception>
        public PEMemoryBlock GetMetadata()
        {
            return new PEMemoryBlock(GetMetadataBlock());
        }

        /// <summary>
        /// Loads PE section that contains the specified <paramref name="relativeVirtualAddress"/> into memory
        /// and returns a memory block that starts at <paramref name="relativeVirtualAddress"/> and ends at the end of the containing section.
        /// </summary>
        /// <param name="relativeVirtualAddress">Relative Virtual Address of the data to read.</param>
        /// <returns>
        /// An empty block if <paramref name="relativeVirtualAddress"/> doesn't represent a location in any of the PE sections of this PE image.
        /// </returns>
        /// <exception cref="BadImageFormatException">The PE headers contain invalid data.</exception>
        public PEMemoryBlock GetSectionData(int relativeVirtualAddress)
        {
            var sectionIndex = PEHeaders.GetContainingSectionIndex(relativeVirtualAddress);
            if (sectionIndex < 0)
            {
                return default(PEMemoryBlock);
            }

            int relativeOffset = relativeVirtualAddress - PEHeaders.SectionHeaders[sectionIndex].VirtualAddress;
            int size = PEHeaders.SectionHeaders[sectionIndex].VirtualSize - relativeOffset;

            AbstractMemoryBlock block;
            if (peImage != null)
            {
                block = GetPESectionBlock(sectionIndex);
            }
            else
            {
                block = GetEntireImageBlock();
                relativeOffset += PEHeaders.SectionHeaders[sectionIndex].PointerToRawData;
            }

            return new PEMemoryBlock(block, relativeOffset);
        }
    }
}
