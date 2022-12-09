using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Logging;
using Org.BouncyCastle.Crypto.Paddings;

namespace Nethermind.Evm
{
    enum SectionDividor : byte
    {
        Terminator = 0,
        CodeSection = 1,
        DataSection = 2,
        TypeSection = 3,
    }
    public class EofHeader
    {
        #region public construction properties
        public int TypeSize { get; set; }
        public int[] CodeSize { get; set; }
        public int CodesSize => CodeSize?.Sum() ?? 0;
        public int DataSize { get; set; }
        public byte Version { get; set; }
        public int HeaderSize => 2 + 1 + (DataSize == 0 ? 0 : (1 + 2)) + (TypeSize == 0 ? 0 : (1 + 2)) + 3 * CodeSize.Length + 1;
        // MagicLength + Version + 1 * (SectionSeparator + SectionSize) + HeaderTerminator = 2 + 1 + 1 * (1 + 2) + 1 = 7
        public int ContainerSize => TypeSize + CodesSize + DataSize;
        #endregion

        #region Equality methods
        public override bool Equals(object? obj)
            => this.GetHashCode() == obj.GetHashCode();
        public override int GetHashCode()
            => CodeSize.GetHashCode() ^ DataSize.GetHashCode() ^ TypeSize.GetHashCode();
        #endregion

        #region Sections Offsets
        public (int Start, int Size) TypeSectionOffsets => (HeaderSize, TypeSize);
        public (int Start, int Size) CodeSectionOffsets => (HeaderSize + TypeSize, CodesSize);
        public (int Start, int Size) DataSectionOffsets => (HeaderSize + TypeSize + CodesSize, DataSize);
        public (int Start, int Size) this[int i] => (CodeSize.Take(i).Sum(), CodeSize[i]);
        #endregion
    }

    public class EvmObjectFormat
    {
        private readonly ILogger _logger = null;
        private bool LoggingEnabled => _logger is not null;
        public EvmObjectFormat(ILogManager loggerManager = null)
            => _logger = loggerManager?.GetClassLogger<EvmObjectFormat>();

        // magic prefix : EofFormatByte is the first byte, EofFormatDiff is chosen to diff from previously rejected contract according to EIP3541
        private const byte EofMagicLength = 2;
        private const byte EofFormatByte = 0xEF;
        private const byte EofFormatDiff = 0x00;
        private byte[] EofMagic => new byte[] { EofFormatByte, EofFormatDiff };

        public bool HasEOFFormat(ReadOnlySpan<byte> code) => code.Length > EofMagicLength && code.StartsWith(EofMagic);
        public bool ExtractHeader(ReadOnlySpan<byte> code, IReleaseSpec spec, out EofHeader header)
        {
            if (!HasEOFFormat(code))
            {
                if (LoggingEnabled)
                {
                    _logger.Trace($"EIP-3540 : Code doesn't start with Magic byte sequence expected {EofMagic.ToHexString(true)} ");
                }
                header = null; return false;
            }

            int codeLen = code.Length;

            int i = EofMagicLength;
            byte EOFVersion = code[i++];

            header = new EofHeader
            {
                Version = EOFVersion
            };

            switch (EOFVersion)
            {
                case 1:
                    return HandleEOF1(spec, code, ref header, codeLen, ref i);
                default:
                    if (LoggingEnabled)
                    {
                        _logger.Trace($"EIP-3540 : Code has wrong EOFn version expected {1} but found {EOFVersion}");
                    }
                    header = null; header = null; return false;
            }
        }

        private bool HandleEOF1(IReleaseSpec spec, ReadOnlySpan<byte> code, ref EofHeader header, int codeLen, ref int i)
        {
            bool continueParsing = true;

            List<int> CodeSections = new();
            int? TypeSections = null;
            int? DataSections = null;

            while (i < codeLen && continueParsing)
            {
                var sectionKind = (SectionDividor)code[i];
                i++;

                switch (sectionKind)
                {
                    case SectionDividor.Terminator:
                        {
                            if (CodeSections.Count == 0 || CodeSections.Any(section => section == 0))
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : CodeSection size must follow a CodeSection, CodeSection length was {header.CodesSize}");
                                }
                                header = null; return false;
                            }

                            if (CodeSections.Count > 1 && CodeSections.Count != (TypeSections / 2))
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-4750: Code Sections count must match TypeSection count, CodeSection count was {CodeSections.Count}, expected {TypeSections / 2}");
                                }
                                header = null; return false;
                            }

                            if (CodeSections.Count > 1024)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-4750 : Code section count limit exceeded only 1024 allowed but found {CodeSections.Count}");
                                }
                                header = null; return false;
                            }

                            header.CodeSize = CodeSections.ToArray();
                            header.TypeSize = TypeSections ?? 0;
                            header.DataSize = DataSections ?? 0;
                            continueParsing = false;
                            break;
                        }
                    case SectionDividor.TypeSection:
                        {
                            if (spec.IsEip4750Enabled)
                            {
                                if (DataSections is not null || CodeSections.Count != 0)
                                {
                                    if (LoggingEnabled)
                                    {
                                        _logger.Trace($"EIP-4750 : TypeSection must be before : CodeSection, DataSection");
                                    }
                                    header = null; return false;
                                }

                                if (TypeSections is not null)
                                {
                                    if (LoggingEnabled)
                                    {
                                        _logger.Trace($"EIP-4750 : container must have at max 1 TypeSection but found more");
                                    }
                                    header = null; return false;
                                }

                                if (i + 2 > codeLen)
                                {
                                    if (LoggingEnabled)
                                    {
                                        _logger.Trace($"EIP-4750: type section code incomplete, failed parsing type section");
                                    }
                                    header = null; return false;
                                }

                                TypeSections = code.Slice(i, 2).ReadEthInt16();
                            }
                            else
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : Encountered incorrect Section-Kind {sectionKind}, correct values are [{SectionDividor.CodeSection}, {SectionDividor.DataSection}, {SectionDividor.Terminator}]");
                                }

                                header = null; return false;
                            }
                            i += 2;
                            break;
                        }
                    case SectionDividor.CodeSection:
                        {
                            if (!spec.IsEip4750Enabled && CodeSections.Count > 0)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : only 1 code section is allowed");
                                }
                                header = null; return false;
                            }

                            if (i + 2 > codeLen)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : container code incomplete, failed parsing code section");
                                }
                                header = null; return false;
                            }

                            var codeSectionSize = code.Slice(i, 2).ReadEthUInt16();
                            CodeSections.Add(codeSectionSize);

                            if (codeSectionSize == 0) // code section must be non-empty (i.e : size > 0)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : CodeSection size must be strictly bigger than 0 but found 0");
                                }
                                header = null; return false;
                            }

                            i += 2;
                            break;
                        }
                    case SectionDividor.DataSection:
                        {
                            // data-section must come after code-section and there can be only one data-section
                            if (CodeSections.Count == 0)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : DataSection size must follow a CodeSection, CodeSection length was {CodeSections.Count}");
                                }
                                header = null; return false;
                            }

                            if (DataSections is not null)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : container must have at max 1 DataSection but found more");
                                }
                                header = null; return false;
                            }

                            if (i + 2 > codeLen)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : container code incomplete, failed parsing data section");
                                }
                                header = null; return false;
                            }

                            DataSections = code.Slice(i, 2).ReadEthUInt16();

                            if (DataSections == 0) // if declared data section must be non-empty
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : DataSection size must be strictly bigger than 0 but found 0");
                                }
                                header = null; return false;
                            }

                            i += 2;
                            break;
                        }
                    default: // if section kind is anything beside a section-limiter or a terminator byte we return false
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-3540 : Encountered incorrect Section-Kind {sectionKind}, correct values are [{SectionDividor.TypeSection}, {SectionDividor.CodeSection}, {SectionDividor.DataSection}, {SectionDividor.Terminator}]");
                            }

                            header = null; return false;
                        }
                }
            }
            var contractBody = code[i..];

            var calculatedCodeLen = header.TypeSize + header.CodesSize + header.DataSize;
            if (spec.IsEip4750Enabled && header.TypeSize != 0 && contractBody.Length > 1 && contractBody[0] != 0 && contractBody[1] != 0)
            {
                if (LoggingEnabled)
                {
                    _logger.Trace($"EIP-4750: Invalid Type Section expected {(0, 0)} but found {(contractBody[0], contractBody[1])}");
                }
                header = null; return false;
            }

            if (contractBody.Length == 0 || calculatedCodeLen != contractBody.Length)
            {
                if (LoggingEnabled)
                {
                    _logger.Trace($"EIP-3540 : SectionSizes indicated in bundeled header are incorrect, or ContainerCode is incomplete");
                }
                header = null; return false;
            }
            return true;
        }

        public bool ValidateEofCode(ReadOnlySpan<byte> code, IReleaseSpec spec) => ExtractHeader(code, spec, out _);
        public bool ValidateInstructions(ReadOnlySpan<byte> code, out EofHeader header, IReleaseSpec spec)
        {
            // check if code is EOF compliant
            if (!spec.IsEip3540Enabled)
            {
                header = null;
                return false;
            }

            if (ExtractHeader(code, spec, out header))
            {
                bool valid = true;
                for (int i = 0; i < header.CodeSize.Length; i++)
                {
                    valid &= ValidateSectionInstructions(ref code, i, header, spec);
                }
                return valid;
            }
            return false;
        }

        public bool ValidateSectionInstructions(ref ReadOnlySpan<byte> container, int sectionId, EofHeader header, IReleaseSpec spec)
        {
            // check if code is EOF compliant
            if (!spec.IsEip3540Enabled)
            {
                return false;
            }

            if (!spec.IsEip3670Enabled)
            {
                return true;
            }

            var (codeSectionBegin, codeSectionSize) = header[sectionId];
            var (typeSectionBegin, typeSectionSize) = header.TypeSectionOffsets;
            ReadOnlySpan<byte> code = container.Slice(header.CodeSectionOffsets.Start + codeSectionBegin, codeSectionSize);
            ReadOnlySpan<byte> typesection = container.Slice(typeSectionBegin, typeSectionSize);
            Instruction? opcode = null;
            HashSet<Range> immediates = new HashSet<Range>();
            HashSet<Int32> rjumpdests = new HashSet<Int32>();
            for (int i = 0; i < codeSectionSize;)
            {
                opcode = (Instruction)code[i];
                i++;
                // validate opcode
                if (!opcode.Value.IsValid(spec))
                {
                    if (LoggingEnabled)
                    {
                        _logger.Trace($"EIP-3670 : CodeSection contains undefined opcode {opcode}");
                    }
                    return false;
                }

                if (spec.IsEip4200Enabled)
                {
                    if (opcode is Instruction.RJUMP or Instruction.RJUMPI)
                    {
                        if (i + 2 > codeSectionSize)
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-4200 : Static Relative Jump Argument underflow");
                            }
                            return false;
                        }

                        var offset = code.Slice(i, 2).ReadEthInt16();
                        immediates.Add(new Range(i, i + 1));
                        var rjumpdest = offset + 2 + i;
                        rjumpdests.Add(rjumpdest);
                        if (rjumpdest < 0 || rjumpdest >= codeSectionSize)
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-4200 : Static Relative Jump Destination outside of Code bounds");
                            }
                            header = null; return false;
                        }
                        i += 2;
                    }

                    if (opcode is Instruction.RJUMPV)
                    {
                        if (i + 2 > codeSectionSize)
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-4200 : Static Relative Jumpv Argument underflow");
                            }
                            header = null; return false;
                        }

                        byte count = code[i];
                        if (count < 1)
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-4200 : jumpv jumptable must have at least 1 entry");
                            }
                            header = null; return false;
                        }
                        if (i + count * 2 > codeSectionSize)
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-4200 : jumpv jumptable underflow");
                            }
                            header = null; return false;
                        }
                        var immediateValueSize = 1 + count * 2;
                        immediates.Add(new Range(i, i + immediateValueSize - 1));
                        for (int j = 0; j < count; j++)
                        {
                            var offset = code.Slice(i + 1 + j * 2, 2).ReadEthInt16();
                            var rjumpdest = offset + immediateValueSize + i;
                            rjumpdests.Add(rjumpdest);
                            if (rjumpdest < 0 || rjumpdest >= codeSectionSize)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-4200 : Static Relative Jumpv Destination outside of Code bounds");
                                }
                                header = null; return false;
                            }
                        }
                        i += immediateValueSize;
                    }
                }

                if (spec.IsEip4750Enabled)
                {
                    if (opcode is Instruction.CALLF)
                    {
                        if (i + 2 > codeSectionSize)
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-4750 : CALLF Argument underflow");
                            }
                            return false;
                        }

                        var targetSectionId = code.Slice(i, 2).ReadEthUInt16();
                        immediates.Add(new Range(i, i + 1));

                        if (targetSectionId >= header.CodeSize.Length)
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-4750 : Invalid Section Id");
                            }
                            return false;
                        }
                        i += 2;
                    }

                    if (opcode is Instruction.JUMPF)
                    {
                        if (i + 2 > codeSectionSize)
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-4750 : CALLF Argument underflow");
                            }
                            return false;
                        }

                        var targetSectionId = code.Slice(i, 2).ReadEthUInt16();
                        immediates.Add(new Range(i, i + 1));

                        if (targetSectionId >= header.CodeSize.Length)
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-4750 : Invalid Section Id");
                            }
                            return false;
                        }

                        if (typesection[targetSectionId * 2 + 1] != typesection[sectionId * 2 + 1])
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-4750 : Incompatible Function Type for JUMPF");
                            }
                            return false;
                        }
                        i += 2;
                    }
                }

                if (opcode is >= Instruction.PUSH1 and <= Instruction.PUSH32)
                {
                    int len = code[i - 1] - (int)Instruction.PUSH1 + 1;
                    immediates.Add(new Range(i, i + len - 1));
                    i += len;
                }
            }

            bool endCorrectly = opcode switch
            {
                Instruction.RETF or Instruction.JUMPF when spec.IsEip4750Enabled => true,
                Instruction.STOP or Instruction.RETURN or Instruction.REVERT or Instruction.INVALID // or Instruction.SELFDESTRUCT
                    => true,
                _ => false
            };

            if (!endCorrectly)
            {
                if (LoggingEnabled)
                {
                    _logger.Trace($"EIP-3670 : Last opcode {opcode} in CodeSection should be either [{Instruction.RETF}, {Instruction.STOP}, {Instruction.RETURN}, {Instruction.REVERT}, {Instruction.INVALID}"); // , {Instruction.SELFDESTRUCT}");
                }
                return false;
            }

            if (spec.IsEip4200Enabled)
            {

                foreach (int rjumpdest in rjumpdests)
                {
                    foreach (var range in immediates)
                    {
                        if (range.Includes(rjumpdest))
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-4200 : Static Relative Jump destination {rjumpdest} is an Invalid, falls within {range}");
                            }
                            return false;
                        }
                    }
                }
            }
            return true;
        }
    }
}
