namespace Microsoft.eShopWeb.ApplicationCore.Constants;

public class AuthorizationConstants
{
    public const string AUTH_KEY = "AuthKeyOfDoomThatMustBeAMinimumNumberOfBytes";

    // TODO: Don't use this in production
    public const string DEFAULT_PASSWORD = "Pass@word1";

    // The JWT signing key is not a constant: every component that signs or validates tokens reads it from
    // configuration (Jwt:Key, set from JWT_KEY in .env).
}
