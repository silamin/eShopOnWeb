using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net.Http;

namespace PublicApiIntegrationTests;

[TestClass]
public class ProgramTest
{
    private static WebApplicationFactory<Program> _application = new();

    public static HttpClient NewClient
    {
        get
        {
            return _application.CreateClient();
        }
    }

    [AssemblyInitialize]
    public static void AssemblyInitialize(TestContext _)
    {
        // The API reads its JWT signing key from configuration; give it the key the test tokens are signed with.
        System.Environment.SetEnvironmentVariable("Jwt__Key", ApiTokenHelper.TestJwtKey);
        _application = new WebApplicationFactory<Program>();

    }
}
