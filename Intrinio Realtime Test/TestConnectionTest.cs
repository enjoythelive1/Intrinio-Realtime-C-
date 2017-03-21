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
            var intrinioRealtime = new IntrinioRealtime.IntrinioRealtime("API USENAME HERE", "API PASSWORD HERE", true);

            var observable = await intrinioRealtime.join("AAPL");
            var quote = await observable.Timeout(new TimeSpan(0, 4, 0)).FirstAsync();

            Assert.IsNotNull(quote);
            Assert.AreEqual(quote.Ticker, "AAPL");
        }
    }
}
