using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.Web.Services;
using Xunit;

namespace Microsoft.eShopWeb.UnitTests.Web.Services;

/// <summary>Krav 5/6: the monolith's basket becomes the shared contract — only contract fields, a price snapshot.</summary>
public class BasketCheckedOutMapperTests
{
    [Fact]
    public void Basket_lines_become_contract_lines_with_the_basket_price_and_catalog_names()
    {
        var basket = new Basket("demouser@microsoft.com");
        basket.AddItem(1, 19.50m, 2);
        basket.AddItem(3, 12.00m, 1);
        var catalog = new List<CatalogItemInfo>
        {
            new(1, ".NET Bot Black Sweatshirt", "", 99m, "/images/products/1.png", 1, 1),   // catalog price differs:
            new(3, "Prism White T-Shirt", "", 12m, "/images/products/3.png", 1, 1),         // the basket's price wins
        };

        var evt = BasketCheckedOutMapper.Map(basket, catalog);

        Assert.Equal("demouser@microsoft.com", evt.BuyerId);
        Assert.NotEqual(Guid.Empty, evt.EventId);
        Assert.Collection(evt.Lines,
            l => { Assert.Equal((1, ".NET Bot Black Sweatshirt", 19.50m, 2), (l.CatalogItemId, l.Name, l.UnitPrice, l.Quantity)); },
            l => { Assert.Equal((3, "Prism White T-Shirt", 12.00m, 1), (l.CatalogItemId, l.Name, l.UnitPrice, l.Quantity)); });
    }
}
