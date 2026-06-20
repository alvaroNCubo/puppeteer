using eShop.Ordering.Domain.AggregatesModel.OrderAggregate;

namespace PuppeteerHost
{
    // Facade callable from the Puppeteer DSL. One DSL invocation cascades
    // into multiple rich-domain calls on the eShop Order aggregate. Kept
    // self-contained in the container image (no external test-project
    // reference at runtime).
    public class OrderingFacade
    {
        public Order NewSubmittedOrder(string userId, string userName)
        {
            var address = new Address("street", "city", "state", "country", "12345");
            return new Order(userId, userName, address, 1, "1234-5678-9012-3456",
                             "123", "Card Holder", System.DateTime.UtcNow.AddYears(1), null, null);
        }
    }
}
