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
        [InitialValue("0x2b76901a263e1b1d6742a7bfd167d3925bbf18c6", ContractParameterType.Hash160)]
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

        /// <summary>
        /// Swap with both AMM and OrderBook
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="amountIn"></param>
        /// <param name="amountOutMin"></param>
        /// <param name="isAtoB"></param>
        /// <param name="deadLine"></param>
        public static bool SwapTokenInForTokenOutWithOrderBook(UInt160 sender, BigInteger amountInMax, BigInteger amountOutMin, bool isAtoB, BigInteger deadLine)
        {
            Assert(Runtime.CheckWitness(sender), "Forbidden");
            Assert((BigInteger)Runtime.Time <= deadLine, "Exceeded the deadline");

            var tokenIn = isAtoB ? TokenA : TokenB;
            var tokenOut = isAtoB ? TokenB : TokenA;

            var quoteDecimals = GetQuoteDecimals(tokenIn, tokenOut);

            var leftIn = amountInMax;
            BigInteger totalOut = 0;
            while (leftIn > 0)
            {
                var bookPrice = GetOrderBookPrice(tokenIn, tokenOut, !isAtoB);
                if (bookPrice == 0) break;

                // First AMM
                var ammReverse = GetReserves(tokenIn, tokenOut);
                var ammPrice = GetAMMPrice(isAtoB ? ammReverse[0] : ammReverse[1], isAtoB ? ammReverse[1] : ammReverse[0], quoteDecimals);
                if ((isAtoB && ammPrice > bookPrice) || (!isAtoB && ammPrice < bookPrice))
                {
                    var amountToPool = GetAmountInTillPrice(!isAtoB, bookPrice, quoteDecimals, ammReverse[0], ammReverse[1]);
                    if (leftIn <= amountToPool)
                    {
                        var amountOut = GetAMMAmountOut(leftIn, ammReverse[0], ammReverse[1]);
                        SafeTransfer(isAtoB ? TokenA : TokenB, sender, SwapPair, leftIn);
                        Swap(amountOut, isAtoB, sender);
                        leftIn = 0;
                        totalOut += amountOut;
                        break;
                    }
                    else
                    {
                        var amountOut = GetAMMAmountOut(amountToPool, ammReverse[0], ammReverse[1]);
                        SafeTransfer(isAtoB ? TokenA : TokenB, sender, SwapPair, amountToPool);
                        Swap(amountOut, isAtoB, sender);
                        leftIn -= amountToPool;
                        totalOut += amountOut;
                    }
                }

                // Then book
                var amountOutBook = GetOrderBookAmountOut(tokenIn, tokenOut, bookPrice, bookPrice, leftIn)[1];
                leftIn = SendSwapOrder(tokenIn, tokenOut, sender, !isAtoB, bookPrice, leftIn);
                totalOut += amountOutBook;
            }

            // Finally AMM
            if (leftIn > 0)
            {
                var ammReverse = GetReserves(tokenIn, tokenOut);
                var amountOut = GetAMMAmountOut(leftIn, ammReverse[0], ammReverse[1]);
                SafeTransfer(isAtoB ? TokenA : TokenB, sender, SwapPair, leftIn);
                Swap(amountOut, isAtoB, sender);
                totalOut += amountOut;
            }
            Assert(totalOut >= amountOutMin, "Insufficient AmountOut");
            return true;
        }

        public static bool SwapTokenOutForTokenInWithOrderBook(UInt160 sender, BigInteger amountOutMin, BigInteger amountInMax, bool isAtoB, BigInteger deadLine)
        {
            Assert(Runtime.CheckWitness(sender), "Forbidden");
            Assert((BigInteger)Runtime.Time <= deadLine, "Exceeded the deadline");

            var tokenIn = isAtoB ? TokenA : TokenB;
            var tokenOut = isAtoB ? TokenB : TokenA;

            var quoteDecimals = GetQuoteDecimals(tokenIn, tokenOut);

            var leftOut = amountOutMin;
            BigInteger totalIn = 0;
            while (leftOut > 0)
            {
                var bookPrice = GetOrderBookPrice(tokenIn, tokenOut, !isAtoB);
                if (bookPrice == 0) break;

                // First AMM
                var ammReverse = GetReserves(tokenIn, tokenOut);
                var ammPrice = GetAMMPrice(isAtoB ? ammReverse[0] : ammReverse[1], isAtoB ? ammReverse[1] : ammReverse[0], quoteDecimals);
                if ((isAtoB && ammPrice > bookPrice) || (!isAtoB && ammPrice < bookPrice))
                {
                    var amountToPool = GetAmountInTillPrice(!isAtoB, bookPrice, quoteDecimals, ammReverse[0], ammReverse[1]);
                    var amountOutPool = GetAMMAmountOut(amountToPool, ammReverse[0], ammReverse[1]);
                    if (amountOutPool >= leftOut)
                    {
                        var amountIn = GetAMMAmountIn(leftOut, ammReverse[0], ammReverse[1]);
                        SafeTransfer(isAtoB ? TokenA : TokenB, sender, SwapPair, amountIn);
                        Swap(leftOut, isAtoB, sender);
                        leftOut = 0;
                        totalIn += amountIn;
                        break;
                    }
                    else
                    {
                        SafeTransfer(isAtoB ? TokenA : TokenB, sender, SwapPair, amountToPool);
                        Swap(amountOutPool, isAtoB, sender);
                        leftOut -= amountOutPool;
                        totalIn += amountToPool;
                    }
                }

                // Then book
                var amountToBook = GetOrderBookAmountIn(tokenIn, tokenOut, bookPrice, bookPrice, leftOut)[1];
                var amountOutBook = GetOrderBookAmountOut(tokenIn, tokenOut, bookPrice, bookPrice, amountToBook)[1];
                Assert(SendSwapOrder(tokenIn, tokenOut, sender, !isAtoB, bookPrice, amountToBook) == 0, "");
                leftOut -= amountOutBook;
                totalIn += amountToBook;
            }

            // Finally AMM
            if (leftOut > 0)
            {
                var ammReverse = GetReserves(tokenIn, tokenOut);
                var amountIn = GetAMMAmountIn(leftOut, ammReverse[0], ammReverse[1]);
                SafeTransfer(isAtoB ? TokenA : TokenB, sender, SwapPair, amountIn);
                Swap(leftOut, isAtoB, sender);
                totalIn += amountIn;
            }
            Assert(totalIn <= amountInMax, "Excessive AmountIn");
            return true;
        }

        /// <summary>
        /// Native Router methods
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="amountIn"></param>
        /// <param name="amountOutMin"></param>
        /// <param name="isAtoB"></param>
        /// <param name="deadLine"></param>
        public static bool SwapTokenInForTokenOut(UInt160 sender, BigInteger amountIn, BigInteger amountOutMin, bool isAtoB, BigInteger deadLine)
        {
            Assert(Runtime.CheckWitness(sender), "Forbidden");
            Assert((BigInteger)Runtime.Time <= deadLine, "Exceeded the deadline");

            var data = isAtoB ? GetReserves(TokenA, TokenB) : GetReserves(TokenB, TokenA);
            var amountOut = GetAMMAmountOut(amountIn, data[0], data[1]);
            Assert(amountOut >= amountOutMin, "Insufficient AmountOut");

            SafeTransfer(isAtoB ? TokenA : TokenB, sender, SwapPair, amountIn);
            Swap(amountOut, isAtoB, sender);
            return true;
        }

        public static bool SwapTokenOutForTokenIn(UInt160 sender, BigInteger amountOut, BigInteger amountInMax, bool isAtoB, BigInteger deadLine)
        {
            Assert(Runtime.CheckWitness(sender), "Forbidden");
            Assert((BigInteger)Runtime.Time <= deadLine, "Exceeded the deadline");

            var data = isAtoB ? GetReserves(TokenA, TokenB) : GetReserves(TokenB, TokenA);
            var amountIn = GetAMMAmountIn(amountOut, data[0], data[1]);
            Assert(amountIn <= amountInMax, "Excessive AmountIn");

            SafeTransfer(isAtoB ? TokenA : TokenB, sender, SwapPair, amountIn);
            Swap(amountOut, isAtoB, sender);
            return true;
        }

        /// <summary>
        /// Get amountOut with both AMM and OrderBook
        /// </summary>
        /// <param name="amountInMax"></param>
        /// <param name="isAtoB"></param>
        [Safe]
        public static BigInteger GetAmountOutWithOrderBook(BigInteger amountInMax, bool isAtoB)
        {
            var tokenIn = isAtoB ? TokenA : TokenB;
            var tokenOut = isAtoB ? TokenB : TokenA;

            var quoteDecimals = GetQuoteDecimals(tokenIn, tokenOut);
            var bookPrice = GetOrderBookPrice(tokenIn, tokenOut, !isAtoB);
            var ammReverse = GetReserves(tokenIn, tokenOut);

            var leftIn = amountInMax;
            BigInteger totalOut = 0;
            while (bookPrice > 0 && leftIn > 0)
            {
                var ammPrice = GetAMMPrice(isAtoB ? ammReverse[0] : ammReverse[1], isAtoB ? ammReverse[1] : ammReverse[0], quoteDecimals);

                // First AMM
                if ((isAtoB && ammPrice > bookPrice) || (!isAtoB && ammPrice < bookPrice))
                {
                    var amountToPool = GetAmountInTillPrice(!isAtoB, bookPrice, quoteDecimals, ammReverse[0], ammReverse[1]);
                    if (leftIn <= amountToPool)
                    {
                        var amountOut = GetAMMAmountOut(leftIn, ammReverse[0], ammReverse[1]);
                        leftIn = 0;
                        totalOut += amountOut;
                        break;
                    }
                    else
                    {
                        var amountOut = GetAMMAmountOut(amountToPool, ammReverse[0], ammReverse[1]);
                        leftIn -= amountToPool;
                        totalOut += amountOut;
                        ammReverse[0] += amountToPool;
                        ammReverse[1] -= amountOut;
                    }
                }

                // Then book
                var result = GetOrderBookAmountOut(tokenIn, tokenOut, bookPrice, bookPrice, leftIn);
                leftIn = result[0];
                totalOut += result[1];
                bookPrice = GetOrderBookNextPrice(tokenIn, tokenOut, !isAtoB, bookPrice);
            }

            // Finally AMM
            if (leftIn > 0)
            {
                totalOut += GetAMMAmountOut(leftIn, ammReverse[0], ammReverse[1]);
            }

            return totalOut;
        }

        /// <summary>
        /// Get amountIn with both AMM and OrderBook
        /// </summary>
        /// <param name="amountOutMin"></param>
        /// <param name="isAtoB"></param>
        [Safe]
        public static BigInteger GetAmountInWithOrderBook(BigInteger amountOutMin, bool isAtoB)
        {
            var tokenIn = isAtoB ? TokenA : TokenB;
            var tokenOut = isAtoB ? TokenB : TokenA;

            var quoteDecimals = GetQuoteDecimals(tokenIn, tokenOut);
            var bookPrice = GetOrderBookPrice(tokenIn, tokenOut, !isAtoB);
            var ammReverse = GetReserves(tokenIn, tokenOut);

            var leftOut = amountOutMin;
            BigInteger totalIn = 0;
            while (bookPrice > 0 && leftOut > 0)
            {
                var ammPrice = GetAMMPrice(isAtoB ? ammReverse[0] : ammReverse[1], isAtoB ? ammReverse[1] : ammReverse[0], quoteDecimals);

                // First AMM
                if ((isAtoB && ammPrice > bookPrice) || (!isAtoB && ammPrice < bookPrice))
                {
                    var amountToPool = GetAmountInTillPrice(!isAtoB, bookPrice, quoteDecimals, ammReverse[0], ammReverse[1]);
                    var amountOutPool = GetAMMAmountOut(amountToPool, ammReverse[0], ammReverse[1]);
                    if (amountOutPool >= leftOut)
                    {
                        var amountIn = GetAMMAmountIn(leftOut, ammReverse[0], ammReverse[1]);
                        leftOut = 0;
                        totalIn += amountIn;
                        break;
                    }
                    else
                    {
                        leftOut -= amountOutPool;
                        totalIn += amountToPool;
                        ammReverse[0] += amountToPool;
                        ammReverse[1] -= amountOutPool;
                    }
                }

                // Then book
                var result = GetOrderBookAmountIn(tokenIn, tokenOut, bookPrice, bookPrice, leftOut);
                leftOut = result[0];
                totalIn += result[1];
                bookPrice = GetOrderBookNextPrice(tokenIn, tokenOut, !isAtoB, bookPrice);
            }

            // Finally AMM
            if (leftOut > 0)
            {
                totalIn += GetAMMAmountIn(leftOut, ammReverse[0], ammReverse[1]);
            }
            return totalIn;
        }

        /// <summary>
        /// Swap until reverseQuote/reverseBase equals price
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="amountInMax"></param>
        /// <param name="amountOutMin"></param>
        /// <param name="price"></param>
        /// <param name="quoteDecimals"></param>
        /// <param name="isAtoB"></param>
        /// <param name="deadLine"></param>
        public static bool SwapTillPrice(UInt160 sender, BigInteger amountInMax, BigInteger amountOutMin, BigInteger price, uint quoteDecimals, bool isAtoB, BigInteger deadLine)
        {
            Assert(Runtime.CheckWitness(sender), "Forbidden");
            Assert((BigInteger)Runtime.Time <= deadLine, "Exceeded the deadline");

            var data = isAtoB ? GetReserves(TokenA, TokenB) : GetReserves(TokenB, TokenA);

            var amountIn = GetAmountInTillPrice(!isAtoB, price, quoteDecimals, data[0], data[1]);
            var amountOut = GetAMMAmountOut(amountIn, data[0], data[1]);

            Assert(amountOut >= amountOutMin, "Insufficient AmountOut", amountOut);
            Assert(amountIn <= amountInMax, "Excessive AmountIn", amountIn);

            SafeTransfer(isAtoB ? TokenA : TokenB, sender, SwapPair, amountIn);
            Swap(amountOut, isAtoB, sender);

            return true;
        }

        /// <summary>
        /// Get amountIn for AMM to reach the book price, based on reverses
        /// </summary>
        /// <param name="isBuy"></param>
        /// <param name="price"></param>
        /// <param name="quoteDecimals"></param>
        /// <param name="reserveIn"></param>
        /// <param name="reserveOut"></param>
        [Safe]
        public static BigInteger GetAmountInTillPrice(bool isBuy, BigInteger price, uint quoteDecimals, BigInteger reserveIn, BigInteger reserveOut)
        {
            var reverseInNew = BigInteger.Pow(reserveIn, 2) * 9 / 1000000;
            if (isBuy) reverseInNew += reserveIn * reserveOut * price * 3988 / BigInteger.Pow(10, (int)quoteDecimals) / 1000;
            else reverseInNew += reserveIn * reserveOut * BigInteger.Pow(10, (int)quoteDecimals) * 3988 / price / 1000;
            reverseInNew = (reverseInNew.Sqrt() - reserveIn * 3 / 1000) * 1000 / 1994;
            return reverseInNew - reserveIn;
        }

        /// <summary>
        /// Native Router methods
        /// </summary>
        /// <param name="amountOut"></param>
        /// <param name="reserveIn"></param>
        /// <param name="reserveOut"></param>
        [Safe]
        public static BigInteger GetAMMAmountIn(BigInteger amountOut, BigInteger reserveIn, BigInteger reserveOut)
        {
            Assert(amountOut > 0 && reserveIn > 0 && reserveOut > 0, "AmountOut Must > 0");
            var numerator = reserveIn * amountOut * 1000;
            var denominator = (reserveOut - amountOut) * 997;
            var amountIn = (numerator / denominator) + 1;
            return amountIn;
        }

        /// <summary>
        /// Native Router methods
        /// </summary>
        /// <param name="amountIn"></param>
        /// <param name="reserveIn"></param>
        /// <param name="reserveOut"></param>
        [Safe]
        public static BigInteger GetAMMAmountOut(BigInteger amountIn, BigInteger reserveIn, BigInteger reserveOut)
        {
            Assert(amountIn > 0 && reserveIn > 0 && reserveOut > 0, "AmountIn Must > 0");
            var amountInWithFee = amountIn * 997;
            var numerator = amountInWithFee * reserveOut;
            var denominator = reserveIn * 1000 + amountInWithFee;
            var amountOut = numerator / denominator;
            return amountOut;
        }

        private static BigInteger[] GetOrderBookAmountIn(UInt160 tokenFrom, UInt160 tokenTo, BigInteger startPrice, BigInteger endPrice, BigInteger amountInMax)
        {
            return (BigInteger[])Contract.Call(OrderBook, "getAmountIn", CallFlags.ReadOnly, new object[] { tokenFrom, tokenTo, startPrice, endPrice, amountInMax });
        }

        private static BigInteger[] GetOrderBookAmountOut(UInt160 tokenFrom, UInt160 tokenTo, BigInteger startPrice, BigInteger endPrice, BigInteger amountOutMin)
        {
            return (BigInteger[])Contract.Call(OrderBook, "getAmountOut", CallFlags.ReadOnly, new object[] { tokenFrom, tokenTo, startPrice, endPrice, amountOutMin });
        }

        private static BigInteger[] MatchSwapOrder(UInt160 tokenFrom, UInt160 tokenTo, UInt160 sender, bool isBuy, BigInteger price, BigInteger amount)
        {
            return (BigInteger[])Contract.Call(OrderBook, "matchOrder", CallFlags.ReadOnly, new object[] { tokenFrom, tokenTo, isBuy, price, amount});
        }

        private static BigInteger SendSwapOrder(UInt160 tokenFrom, UInt160 tokenTo, UInt160 sender, bool isBuy, BigInteger price, BigInteger amount)
        {
            return (BigInteger)Contract.Call(OrderBook, "dealMarketOrder", CallFlags.All, new object[] { tokenFrom, tokenTo, sender, isBuy, price, amount });
        }

        private static void Swap(BigInteger amountOut, bool isAtoB, UInt160 toAddress)
        {
            var input = isAtoB ? TokenA : TokenB;
            var output = isAtoB ? TokenB : TokenA;

            if (input.ToUInteger() < output.ToUInteger())
            {
                Contract.Call(SwapPair, "swap", CallFlags.All, new object[] { 0, amountOut, toAddress, null });
            }
            else
            {
                Contract.Call(SwapPair, "swap", CallFlags.All, new object[] { amountOut, 0, toAddress, null });
            }
        }

        public static BigInteger GetAMMPrice(uint quoteDecimals)
        {
            var reserves = GetReserves(TokenA, TokenB);
            return GetAMMPrice(reserves[0], reserves[1], quoteDecimals);
        }

        private static BigInteger GetAMMPrice(BigInteger reverseBase, BigInteger reverseQuote, uint quoteDecimals)
        {
            return reverseQuote * BigInteger.Pow(10, (int)quoteDecimals) / reverseBase;
        }

        public static BigInteger GetOrderBookPrice(UInt160 tokenFrom, UInt160 tokenTo, bool isBuy)
        {
            return (BigInteger)Contract.Call(OrderBook, "getMarketPrice", CallFlags.ReadOnly, new object[] { tokenFrom, tokenTo, isBuy });
        }

        public static BigInteger GetOrderBookNextPrice(UInt160 tokenFrom, UInt160 tokenTo, bool isBuy, BigInteger price)
        {
            return (BigInteger)Contract.Call(OrderBook, "getNextPrice", CallFlags.ReadOnly, new object[] { tokenFrom, tokenTo, isBuy, price });
        }

        private static BigInteger[] GetReserves(UInt160 tokenA, UInt160 tokenB)
        {
            var reserveData = (ReservesData)Contract.Call(SwapPair, "getReserves", CallFlags.ReadOnly, new object[] { });
            return tokenA.ToUInteger() < tokenB.ToUInteger() ? new BigInteger[] { reserveData.Reserve0, reserveData.Reserve1 } : new BigInteger[] { reserveData.Reserve1, reserveData.Reserve0 };
        }

        private static uint GetQuoteDecimals(UInt160 tokenFrom, UInt160 tokenTo)
        {
            return (uint)Contract.Call(OrderBook, "getQuoteDecimals", CallFlags.ReadOnly, new object[] { tokenFrom, tokenTo });
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
