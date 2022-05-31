using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Services;
using System;
using System.ComponentModel;
using System.Numerics;

namespace PriceAggregator
{
    [DisplayName("PriceAggregator")]
    [ManifestExtra("Author", "NEO")]
    [ManifestExtra("Email", "developer@neo.org")]
    [ManifestExtra("Description", "This is a PriceAggregator")]
    [ContractPermission("*")]
    public class PriceAggregator : SmartContract
    {
        [InitialValue("0xc80f0323bab04b95e20989004e9c7b6d1d52b39e", ContractParameterType.Hash160)]
        static readonly UInt160 OrderBook = default;

        [InitialValue("0x4ca98851d402336d44f264a7ce7b1b4443a2f53f", ContractParameterType.Hash160)]
        static readonly UInt160 SwapPair = default;

        [InitialValue("0x387d9517c4bab4327b2f402bbe5852cbf5444c21", ContractParameterType.Hash160)] // base token Fox
        static readonly UInt160 TokenA = default;

        [InitialValue("0xa2a5e67ca1f4bc0f891196877b13688df20aff37", ContractParameterType.Hash160)] // quote token Berry
        static readonly UInt160 TokenB = default;

        public struct ReservesData
        {
            public BigInteger Reserve0;
            public BigInteger Reserve1;
            public BigInteger BlockTimestampLast;
        }

        [DisplayName("Fault")]
        public static event FaultEvent onFault;
        public delegate void FaultEvent(string message, params object[] paras);

        public static bool SwapTokenInForTokenOutWithOrderBook(UInt160 sender, BigInteger amountIn, BigInteger amountOutMin, bool isAtoB, BigInteger deadLine)
        {
            return false;
        }

        public static bool SwapTokenOutForTokenInWithOrderBook(UInt160 sender, BigInteger amountOut, BigInteger amountInMax, bool isAtoB, BigInteger deadLine)
        {
            return false;
        }

        public static bool SwapTokenInForTokenOut(UInt160 sender, BigInteger amountIn, BigInteger amountOutMin, bool isAtoB, BigInteger deadLine)
        {
            Assert(Runtime.CheckWitness(sender), "Forbidden");
            Assert((BigInteger)Runtime.Time <= deadLine, "Exceeded the deadline");

            var data = isAtoB ? GetReserves(TokenA, TokenB) : GetReserves(TokenB, TokenA);
            var amountOut = GetAmountOut(amountIn, data[0], data[1]);
            Assert(amountOut >= amountOutMin, "Insufficient AmountOut");

            SafeTransfer(isAtoB ? TokenA : TokenB, sender, SwapPair, amountIn);
            Swap(amountIn, amountOut, isAtoB, sender);
            return true;
        }

        public static bool SwapTokenOutForTokenIn(UInt160 sender, BigInteger amountOut, BigInteger amountInMax, bool isAtoB, BigInteger deadLine)
        {
            Assert(Runtime.CheckWitness(sender), "Forbidden");
            Assert((BigInteger)Runtime.Time <= deadLine, "Exceeded the deadline");

            var data = isAtoB ? GetReserves(TokenA, TokenB) : GetReserves(TokenB, TokenA);
            var amountIn = GetAmountIn(amountOut, data[0], data[1]);
            Assert(amountIn <= amountInMax, "Excessive AmountIn");

            SafeTransfer(isAtoB ? TokenA : TokenB, sender, SwapPair, amountIn);
            Swap(amountIn, amountOut, isAtoB, sender);
            return true;
        }

        public static bool SwapTillPrice(UInt160 sender, BigInteger amountInMax, BigInteger amountOutMin, BigInteger price, uint quoteDecimals, bool isAtoB, BigInteger deadLine)
        {
            Assert(Runtime.CheckWitness(sender), "Forbidden");
            Assert((BigInteger)Runtime.Time <= deadLine, "Exceeded the deadline");

            BigInteger amountIn = 0;
            BigInteger amountOut = 0;

            var data = isAtoB ? GetReserves(TokenA, TokenB) : GetReserves(TokenB, TokenA);

            amountIn = GetAmountInTillPrice(!isAtoB, price, quoteDecimals, data[0], data[1]);
            amountOut = GetAmountOut(amountIn, data[0], data[1]);

            Assert(amountOut >= amountOutMin, "Insufficient AmountOut", amountOut);
            Assert(amountIn <= amountInMax, "Excessive AmountIn", amountIn);

            SafeTransfer(isAtoB ? TokenA : TokenB, sender, SwapPair, amountIn);
            Swap(amountIn, amountOut, isAtoB, sender);

            return true;
        }

        public static BigInteger GetAMMPrice(uint quoteDecimals)
        {
            var reserves = GetReserves(TokenA, TokenB);
            return reserves[1] * BigInteger.Pow(10, (int)quoteDecimals) / reserves[0];
        }

        public static BigInteger GetAmountInTillPrice(bool isBuy, BigInteger price, uint quoteDecimals, BigInteger reserveIn, BigInteger reserveOut)
        {
            BigInteger reverseInNew;
            if (isBuy) reverseInNew = BigInteger.Pow(reserveIn, 2) * 9 / 1000000 + reserveIn * reserveOut * price * 3988 / BigInteger.Pow(10, (int)quoteDecimals) / 1000;
            else reverseInNew = BigInteger.Pow(reserveIn, 2) * 9 / 1000000 + reserveIn * reserveOut * BigInteger.Pow(10, (int)quoteDecimals) * 3988 / price / 1000;
            reverseInNew = (reverseInNew.Sqrt() - reserveIn * 3 / 1000) * 1000 / 1994;
            return reverseInNew - reserveIn;
        }

        public static BigInteger GetAmountIn(BigInteger amountOut, BigInteger reserveIn, BigInteger reserveOut)
        {
            Assert(amountOut > 0 && reserveIn > 0 && reserveOut > 0, "AmountOut Must > 0");
            var numerator = reserveIn * amountOut * 1000;
            var denominator = (reserveOut - amountOut) * 997;
            var amountIn = (numerator / denominator) + 1;
            return amountIn;
        }

        public static BigInteger GetAmountOut(BigInteger amountIn, BigInteger reserveIn, BigInteger reserveOut)
        {
            Assert(amountIn > 0 && reserveIn > 0 && reserveOut > 0, "AmountIn Must > 0");
            var amountInWithFee = amountIn * 997;
            var numerator = amountInWithFee * reserveOut;
            var denominator = reserveIn * 1000 + amountInWithFee;
            var amountOut = numerator / denominator;
            return amountOut;
        }

        private static void Swap(BigInteger amountIn, BigInteger amountOut, bool isAtoB, UInt160 toAddress)
        {
            BigInteger amount0Out = 0;
            BigInteger amount1Out = 0;

            UInt160 input;
            UInt160 output;
            if (isAtoB)
            {
                input = TokenA;
                output = TokenB;
            }
            else
            {
                input = TokenB;
                output = TokenA;
            }

            if (input.ToUInteger() < output.ToUInteger())
            {
                amount1Out = amountOut;
            }
            else
            {
                amount0Out = amountOut;
            }

            Contract.Call(SwapPair, "swap", CallFlags.All, new object[] { amount0Out, amount1Out, toAddress, null });
        }

        public static BigInteger[] GetReserves(UInt160 tokenA, UInt160 tokenB)
        {
            var reserveData = (ReservesData)Contract.Call(SwapPair, "getReserves", CallFlags.All, new object[] { });
            return tokenA.ToUInteger() < tokenB.ToUInteger() ? new BigInteger[] { reserveData.Reserve0, reserveData.Reserve1 } : new BigInteger[] { reserveData.Reserve1, reserveData.Reserve0 };
        }

        private static void SafeTransfer(UInt160 token, UInt160 from, UInt160 to, BigInteger amount)
        {
            try
            {
                var result = (bool)Contract.Call(token, "transfer", CallFlags.All, new object[] { from, to, amount, null });
                Assert(result, "Transfer Fail in Router", token);
            }
            catch (Exception)
            {
                Assert(false, "Transfer Error in Router", token);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                onFault(message, null);
                ExecutionEngine.Assert(false);
            }
        }

        private static void Assert(bool condition, string message, params object[] data)
        {
            if (!condition)
            {
                onFault(message, data);
                ExecutionEngine.Assert(false);
            }
        }
    }
}
