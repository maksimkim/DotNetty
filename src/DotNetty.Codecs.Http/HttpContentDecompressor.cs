﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Codecs.Http
{
    using DotNetty.Codecs.Compression;
    using DotNetty.Common.Utilities;
    using DotNetty.Transport.Channels.Embedded;

    public class HttpContentDecompressor : HttpContentDecoder
    {
        protected override EmbeddedChannel NewContentDecoder(ICharSequence contentEncoding)
        {
            if (HttpHeaderValues.Gzip.ContentEqualsIgnoreCase(contentEncoding) 
                || HttpHeaderValues.XGzip.ContentEqualsIgnoreCase(contentEncoding))
            {
                return new EmbeddedChannel(this.HandlerContext.Channel.Id, this.HandlerContext.Channel.Metadata.HasDisconnect, this.HandlerContext.Channel.Configuration, 
                    ZlibCodecFactory.NewZlibDecoder(ZlibWrapper.Gzip));
            }

            if (HttpHeaderValues.Deflate.ContentEqualsIgnoreCase(contentEncoding) 
                || HttpHeaderValues.XDeflate.ContentEqualsIgnoreCase(contentEncoding))
            {
                return new EmbeddedChannel(this.HandlerContext.Channel.Id, this.HandlerContext.Channel.Metadata.HasDisconnect, this.HandlerContext.Channel.Configuration,
                        ZlibCodecFactory.NewZlibDecoder(ZlibWrapper.Zlib));
            }

            // 'identity' or unsupported
            return null;
        }
    }
}
