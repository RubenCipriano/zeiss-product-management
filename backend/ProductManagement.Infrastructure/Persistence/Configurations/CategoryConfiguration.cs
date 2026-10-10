using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProductManagement.Core.Categories;

namespace ProductManagement.Infrastructure.Persistence.Configurations;

/// <summary>
/// How <see cref="Category"/> maps to the <c>categories</c> table, plus the six fixed categories. EF Core reads this
/// configuration at startup, and migrations are generated from it.
/// </summary>
internal class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories");

        builder.HasKey(category => category.Id);

        builder.Property(category => category.Id)
            .ValueGeneratedNever();

        builder.Property(category => category.Name)
            .HasMaxLength(Category.NameMaxLength)
            .IsRequired();

        builder.HasIndex(category => category.Name)
            .IsUnique();

        // Reference data belongs to the schema, so it is seeded through migrations (HasData) and exists in
        // every environment. Sample products, in contrast, are optional and seeded at runtime (SampleDataSeeder).
        builder.HasData(
            new Category(1, "Spectacle Lenses"),
            new Category(2, "Frames"),
            new Category(3, "Sunglasses"),
            new Category(4, "Contact Lenses"),
            new Category(5, "Lens Care"),
            new Category(6, "Accessories"));
    }
}
