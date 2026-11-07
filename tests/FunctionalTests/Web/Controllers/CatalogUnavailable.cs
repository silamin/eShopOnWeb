using System.Net;
using System.Threading.Tasks;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Microsoft.eShopWeb.FunctionalTests.Web.Controllers;

[Collection("Sequential")]
public class CatalogUnavailable : IClassFixture<TestApplication>
{
    private readonly TestApplication _factory;

    public CatalogUnavailable(TestApplication factory) => _factory = factory;

    [Fact]
    public async Task CatalogPage_Returns503_WithClearMessage_WhenCatalogServiceIsDown()
    {
        var client = _factory.CreateClient();
        var fake = (FakeCatalogClient)_factory.Services.GetRequiredService<ICatalogClient>();
        fake.Down = true;
        try
        {
            var response = await client.GetAsync("/");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("catalog is unavailable", body);
            Assert.Contains("basket has not been changed", body);
        }
        finally
        {
            fake.Down = false;
        }
    }
}
