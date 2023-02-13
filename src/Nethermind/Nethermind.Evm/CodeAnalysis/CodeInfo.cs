// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis
{
    public class CodeInfo : ICodeInfo
    {
        private const int SampledCodeLength = 10_001;
        private const int PercentageOfPush1 = 40;
        private const int NumberOfSamples = 100;
        private static readonly Random _rand = new();
        private ICodeInfoAnalyzer? _analyzer;

        public byte[] MachineCode { get; }
        public IPrecompile? Precompile { get; }
        public ReadOnlyMemory<byte> CodeSection => MachineCode;

        public CodeInfo(byte[] code)
        {
            MachineCode = code;
        }

        public int SectionOffset(int _) => 0;

        public bool IsPrecompile => Precompile is not null;

        public CodeInfo(IPrecompile precompile)
        {
            Precompile = precompile;
            MachineCode = Array.Empty<byte>();
        }

        public bool ValidateJump(int destination, bool isSubroutine)
        {
            _analyzer ??= CreateAnalyzer(CodeSection);
            return _analyzer.ValidateJump(destination, isSubroutine);
        }

        /// <summary>
        /// Do sampling to choose an algo when the code is big enough.
        /// When the code size is small we can use the default analyzer.
        /// </summary>
        public static ICodeInfoAnalyzer CreateAnalyzer(ReadOnlyMemory<byte> codeToBeAnalyzed)
        {
            if (codeToBeAnalyzed.Length >= SampledCodeLength)
            {
                ReadOnlySpan<byte> code = codeToBeAnalyzed.Span;
                byte push1Count = 0;

                // we check (by sampling randomly) how many PUSH1 instructions are in the code
                for (int i = 0; i < NumberOfSamples; i++)
                {
                    byte instruction = code[_rand.Next(0, code.Length)];

                    // PUSH1
                    if (instruction == 0x60)
                    {
                        push1Count++;
                    }
                }

                // If there are many PUSH1 ops then use the JUMPDEST analyzer.
                // The JumpdestAnalyzer can perform up to 40% better than the default Code Data Analyzer
                // in a scenario when the code consists only of PUSH1 instructions.
                return push1Count > PercentageOfPush1 ? new JumpdestAnalyzer(codeToBeAnalyzed) : new CodeDataAnalyzer(codeToBeAnalyzed);
            }
            else
            {
                return new CodeDataAnalyzer(codeToBeAnalyzed);
            }
        }
    }
}
