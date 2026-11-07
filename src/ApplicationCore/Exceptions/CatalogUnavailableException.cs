using System;

namespace Microsoft.eShopWeb.ApplicationCore.Exceptions;

/// <summary>The Catalog service did not answer in time or answered with a server error.</summary>
public class CatalogUnavailableException : Exception
{
    public CatalogUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
