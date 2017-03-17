using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading;
using System.Threading.Tasks;

namespace Intrinio_Realtime_Test
{
    [TestClass]
    public class TestConnectionTest
    {
        [TestMethod]
        public async Task CanConnectToTicker()
        {
            var intrinioRealtime = new Intrinio_Realtime.IntrinioRealtime("fe0560fe47150ed7c28f361b0dd24c30", "7907ac944e04504d9ea2a6ef1f94b837", true);

            var observable = await intrinioRealtime.join("AAPL");
            var quote = await observable.Timeout(new TimeSpan(0, 4, 0)).FirstAsync();

            Assert.IsNotNull(quote);
            Assert.AreEqual(quote.Ticker, "AAPL");
        }
    }
}
