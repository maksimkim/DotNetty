﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Codecs.Http
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using DotNetty.Codecs;
    using DotNetty.Common.Utilities;

    public class DefaultHttpHeaders : HttpHeaders
    {
        const int HighestInvalidValueCharMask = ~15;

        static readonly HeaderValueConverter DefaultHeaderValueConverter = new HeaderValueConverter();
        static readonly HeaderValueConverterAndValidator DefaultHeaderValueConverterAndValidator = new HeaderValueConverterAndValidator();

        internal static readonly INameValidator<ICharSequence> HttpNameValidator = new HeaderNameValidator();
        internal static readonly INameValidator<ICharSequence> NotNullValidator = new NullNameValidator<ICharSequence>();

        sealed class HeaderNameValidator : ByteProcessor, INameValidator<ICharSequence>
        {
            public void ValidateName(ICharSequence name)
            {
                if (name == null || name.Count == 0)
                {
                    ThrowHelper.ThrowArgumentException($"empty headers are not allowed [{name}]");
                }
                if (name is AsciiString asciiString)
                {
                    asciiString.ForEachByte(this);
                }
                else
                {
                    // Go through each character in the name
                    Debug.Assert(name != null);
                    // ReSharper disable once ForCanBeConvertedToForeach
                    // Avoid new enumerator instance
                    for (int index = 0; index < name.Count; ++index)
                    {
                        ValidateHeaderNameElement(name[index]);
                    }
                }
            }

            public override bool Process(byte value)
            {
                ValidateHeaderNameElement(value);
                return true;
            }
        }

        readonly DefaultHeaders<ICharSequence, ICharSequence> headers;

        public DefaultHttpHeaders(bool validate = true) : this(validate, NameValidator(validate))
        {
        }

        protected DefaultHttpHeaders(bool validate, INameValidator<ICharSequence> nameValidator) 
            : this(new DefaultHeaders<ICharSequence, ICharSequence>(AsciiString.CaseInsensitiveHasher, ValueConverter(validate), nameValidator))
        {
        }

        protected DefaultHttpHeaders(DefaultHeaders<ICharSequence, ICharSequence> headers)
        {
            this.headers = headers;
        }

        public override HttpHeaders Add(HttpHeaders httpHeaders)
        {
            if (httpHeaders is DefaultHttpHeaders defaultHttpHeaders)
            {
                this.headers.Add(defaultHttpHeaders.headers);
                return this;
            }

            return base.Add(httpHeaders);
        }

        public override HttpHeaders Set(HttpHeaders httpHeaders)
        {
            if (httpHeaders is DefaultHttpHeaders defaultHttpHeaders)
            {
                this.headers.Set(defaultHttpHeaders.headers);
                return this;
            }

            return base.Set(httpHeaders);
        }

        public override HttpHeaders Add(ICharSequence name, object value)
        {
            this.headers.AddObject(name, value);
            return this;
        }

        public override HttpHeaders AddInt(ICharSequence name, int value)
        {
            this.headers.AddInt(name, value);
            return this;
        }

        public override HttpHeaders AddShort(ICharSequence name, short value)
        {
            this.headers.AddShort(name, value);
            return this;
        }

        public override HttpHeaders Remove(ICharSequence name)
        {
            this.headers.Remove(name);
            return this;
        }

        public override HttpHeaders Set(ICharSequence name, object value)
        {
            this.headers.SetObject(name, value);
            return this;
        }

        public override HttpHeaders Set(ICharSequence name, IEnumerable<object> values)
        {
            this.headers.SetObject(name, values);
            return this;
        }

        public override HttpHeaders SetInt(ICharSequence name, int value)
        {
            this.headers.SetInt(name, value);
            return this;
        }

        public override HttpHeaders SetShort(ICharSequence name, short value)
        {
            this.headers.SetShort(name, value);
            return this;
        }

        public override HttpHeaders Clear()
        {
            this.headers.Clear();
            return this;
        }

        public override ICharSequence Get(ICharSequence name) => this.headers.Get(name);

        public override int? GetInt(ICharSequence name) => this.headers.GetInt(name);

        public override int GetInt(ICharSequence name, int defaultValue) => this.headers.GetInt(name, defaultValue);

        public override short? GetShort(ICharSequence name) => this.headers.GetShort(name);

        public override short GetShort(ICharSequence name, short defaultValue) => this.headers.GetShort(name, defaultValue);

        public override long? GetTimeMillis(ICharSequence name) => this.headers.GetTimeMillis(name);

        public override long GetTimeMillis(ICharSequence name, long defaultValue) => this.headers.GetTimeMillis(name, defaultValue);

        public override IList<ICharSequence> GetAll(ICharSequence name) => this.headers.GetAll(name);

        public override bool Contains(ICharSequence name) => this.headers.Contains(name);

        public override bool IsEmpty => this.headers.IsEmpty;

        public override int Size => this.headers.Size;

        public override bool Contains(ICharSequence name, ICharSequence value, bool ignoreCase) =>  this.headers.Contains(name, value, 
            ignoreCase ? AsciiString.CaseInsensitiveHasher : AsciiString.CaseSensitiveHasher);

        public override ISet<ICharSequence> Names() => this.headers.Names();

        public override bool Equals(object obj)
        {
            if (obj is DefaultHttpHeaders other)
            {
                return this.headers.Equals(other.headers, AsciiString.CaseSensitiveHasher);
            }

            return false;
        }

        public override int GetHashCode() => this.headers.HashCode(AsciiString.CaseSensitiveHasher);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void ValidateHeaderNameElement(byte value)
        {
            switch (value)
            {
                case 0x00:
                case 0x09: //'\t':
                case 0x0a: //'\n':
                case 0x0b:
                case 0x0c: //'\f':
                case 0x0d: //'\r':
                case 0x20: //' ':
                case 0x2c: //',':
                case 0x3a: //':':
                case 0x3b: //';':
                case 0x3d: //'=':
                    ThrowHelper.ThrowArgumentException($"a header name cannot contain the following prohibited characters: =,;: \\t\\r\\n\\v\\f: {value}");
                    return;
            }

            // Check to see if the character is not an ASCII character, or invalid
            if (value > 127)
            {
                ThrowHelper.ThrowArgumentException($"a header name cannot contain non-ASCII character: {value}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void ValidateHeaderNameElement(char value)
        {
            switch (value)
            {
                case '\x00':
                case '\t':
                case '\n':
                case '\x0b':
                case '\f':
                case '\r':
                case ' ':
                case ',':
                case ':':
                case ';':
                case '=':
                    ThrowHelper.ThrowArgumentException($"a header name cannot contain the following prohibited characters: =,;: \\t\\r\\n\\v\\f: {value}");
                    return;
            }

            // Check to see if the character is not an ASCII character, or invalid
            if (value > 127)
            {
                ThrowHelper.ThrowArgumentException($"a header name cannot contain non-ASCII character: {value}");
            }
        }

        protected static IValueConverter<ICharSequence> ValueConverter(bool validate) => validate ? DefaultHeaderValueConverterAndValidator : DefaultHeaderValueConverter;

        protected static INameValidator<ICharSequence> NameValidator(bool validate) => validate ? HttpNameValidator : NotNullValidator;

        class HeaderValueConverter : CharSequenceValueConverter
        {
            public override ICharSequence ConvertObject(object value)
            {
                if (value is ICharSequence seq)
                {
                    return seq;
                }

                if (value is DateTime time)
                {
                    return new StringCharSequence(DateFormatter.Format(time));
                }

                return new StringCharSequence(value.ToString());
            }
        }

        sealed class HeaderValueConverterAndValidator : HeaderValueConverter
        {
            public override ICharSequence ConvertObject(object value)
            {
                ICharSequence seq = base.ConvertObject(value);
                int state = 0;

                // Start looping through each of the character
                // ReSharper disable once ForCanBeConvertedToForeach
                // Avoid enumerator allocation
                for (int index = 0; index < seq.Count; index++)
                {
                    state = ValidateValueChar(state, seq[index]);
                }

                if (state != 0)
                {
                    ThrowHelper.ThrowArgumentException($"a header value must not end with '\\r' or '\\n':{seq}");
                }

                return seq;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static int ValidateValueChar(int state, char character)
            {
                /*
                 * State:
                 * 0: Previous character was neither CR nor LF
                 * 1: The previous character was CR
                 * 2: The previous character was LF
                 */
                if ((character & HighestInvalidValueCharMask) == 0)
                {
                    // Check the absolutely prohibited characters.
                    switch (character)
                    {
                        case '\x00': // NULL
                            ThrowHelper.ThrowArgumentException("a header value contains a prohibited character '\0'");
                            break;
                        case '\x0b': // Vertical tab
                            ThrowHelper.ThrowArgumentException("a header value contains a prohibited character '\\v'");
                            break;
                        case '\f':
                            ThrowHelper.ThrowArgumentException("a header value contains a prohibited character '\\f'");
                            break;
                    }
                }

                // Check the CRLF (HT | SP) pattern
                switch (state)
                {
                    case 0:
                        switch (character)
                        {
                            case '\r':
                                return 1;
                            case '\n':
                                return 2;
                        }
                        break;
                    case 1:
                        switch (character)
                        {
                            case '\n':
                                return 2;
                            default:
                                ThrowHelper.ThrowArgumentException("only '\\n' is allowed after '\\r'");
                                break;
                        }
                        break;
                    case 2:
                        switch (character)
                        {
                            case '\t':
                            case ' ':
                                return 0;
                            default:
                                ThrowHelper.ThrowArgumentException("only ' ' and '\\t' are allowed after '\\n'");
                                break;
                        }
                        break;
                }

                return state;
            }
        }


        public override IEnumerator<HeaderEntry<ICharSequence, ICharSequence>> GetEnumerator() => this.headers.GetEnumerator();
    }
}
