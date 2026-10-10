using Microsoft.EntityFrameworkCore;
using ProductManagement.Core.Products;
using ProductManagement.Infrastructure.Products;

namespace ProductManagement.Infrastructure.Persistence.Seeding;

/// <summary>
/// Populates an empty database with sample products for local development and manual testing.
/// </summary>
/// <remarks>
/// Hooked into EF Core's <c>UseSeeding</c>/<c>UseAsyncSeeding</c>, so it runs after migrations are applied, both
/// at startup and via <c>dotnet ef database update</c>. It is idempotent: if any product exists, it does nothing.
/// Identifiers come from the same sequence as API-created products, so seeded rows can never collide with them.
/// </remarks>
internal class SampleDataSeeder(TimeProvider timeProvider)
{
    private static readonly SampleProduct[] SampleProducts =
    [
        new("Single Vision Lens 1.5", "Standard index single vision lens for everyday use.", 49.90m, 320, 1),
        new("Single Vision Lens 1.67", "Thin high-index single vision lens.", 119.00m, 140, 1),
        new("Progressive Lens Comfort", "Entry-level progressive lens with smooth transitions.", 249.00m, 85, 1),
        new("Progressive Lens Precision", "Premium progressive lens with wide reading zones.", 459.00m, 12, 1),
        new("Office Lens Desk", "Near-to-intermediate lens optimised for screen work.", 189.00m, 0, 1),
        new("Blue Light Filter Lens", "Single vision lens with blue-violet light filtering coating.", 89.00m, 210, 1),
        new("Photochromic Lens Grey", "Lens that darkens automatically in sunlight.", 159.00m, 4, 1),
        new("Titanium Full-Rim Frame", "Lightweight titanium frame, matte black.", 179.00m, 45, 2),
        new("Acetate Round Frame", "Classic round acetate frame, tortoise shell.", 129.00m, 60, 2),
        new("Rimless Frame Air", "Minimalist rimless frame with flexible temples.", 219.00m, 7, 2),
        new("Kids Flex Frame", "Durable, flexible frame for children.", 69.00m, 150, 2),
        new("Aviator Sunglasses Polarized", "Polarized aviator sunglasses with UV400 protection.", 149.00m, 38, 3),
        new("Sport Wrap Sunglasses", "Wrap-around sport sunglasses with impact-resistant lenses.", 99.00m, 0, 3),
        new("Clip-On Sun Lenses", "Magnetic clip-on sun lenses for prescription frames.", 39.00m, 95, 3),
        new("Daily Contact Lenses (30 pack)", "Daily disposable soft contact lenses.", 29.90m, 500, 4),
        new("Monthly Contact Lenses (6 pack)", "Monthly replacement silicone hydrogel lenses.", 54.90m, 230, 4),
        new("Toric Contact Lenses (6 pack)", "Monthly lenses for astigmatism correction.", 64.90m, 3, 4),
        new("Lens Cleaning Spray 30 ml", "Alcohol-free cleaning spray for all lens coatings.", 7.90m, 800, 5),
        new("Lens Cleaning Wipes (52 pack)", "Individually wrapped pre-moistened wipes.", 9.90m, 640, 5),
        new("Contact Lens Solution 360 ml", "Multi-purpose solution for soft contact lenses.", 12.50m, 18, 5),
        new("Microfibre Cleaning Cloth", "Reusable lint-free microfibre cloth.", 4.50m, 1200, 6),
        new("Hard Shell Glasses Case", "Protective hard shell case with soft lining.", 14.90m, 260, 6),
        new("Sports Strap", "Adjustable retainer strap for active use.", 8.90m, 9, 6),
        new("Anti-Fog Treatment Gel", "Long-lasting anti-fog gel for lenses.", 11.90m, 0, 6),
    ];

    public async Task SeedAsync(DbContext context, CancellationToken cancellationToken)
    {
        var products = context.Set<Product>();
        if (await products.AnyAsync(cancellationToken))
        {
            return;
        }

        var idGenerator = new PostgresProductIdGenerator((ProductManagementDbContext)context);
        var now = timeProvider.GetUtcNow();

        foreach (var sample in SampleProducts)
        {
            var product = sample.ToProduct(await idGenerator.NextIdAsync(cancellationToken), now);
            products.Add(product);
            context.Set<StockMovement>().Add(StockMovement.ForInitialStock(product, "seed", requestKey: null));
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Synchronous variant, used by the synchronous <c>Migrate()</c> that <c>dotnet ef database update</c> calls.
    /// </summary>
    public void Seed(DbContext context) =>
        SeedAsync(context, CancellationToken.None).GetAwaiter().GetResult();

    private record SampleProduct(
        string Name,
        string Description,
        decimal Price,
        int StockQuantity,
        int CategoryId)
    {
        public Product ToProduct(int id, DateTimeOffset now) =>
            Product.Create(id, Name, Description, Price, StockQuantity, CategoryId, now);
    }
}
