using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Dse.Tests;

internal static class AuthorityFixture
{
    // Test-only explicit bootstrap, using exactly the test's reviewed bindings/grants.
    public static Task InitializeAsync(IRecordStore store, IConfiguration config, string tenant) =>
        AuthorityProvider.InitializeAsync(store,tenant,config.GetSection("Auth:Bindings").Get<IdentityBinding[]>() ?? [],
            config.GetSection("Auth:Grants").Get<AccessGrant[]>() ?? [],default);
}
