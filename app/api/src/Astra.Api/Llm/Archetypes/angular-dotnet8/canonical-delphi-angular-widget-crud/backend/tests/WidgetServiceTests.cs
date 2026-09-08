using Demo.WidgetApi.Models;
using Demo.WidgetApi.Services;
using Xunit;

namespace Demo.WidgetApi.Tests;

public sealed class WidgetServiceTests
{
    private static WidgetService NewService() => new(new InMemoryWidgetRecordPort());

    [Fact]
    public void Create_AssignsIdAndStoresWidget()
    {
        var service = NewService();

        var created = service.Create(new NewWidget("Bolt", 10));

        Assert.True(created.Id > 0);
        Assert.Equal("Bolt", created.Name);
        Assert.Equal(10, created.Quantity);
        Assert.Contains(service.List(), w => w.Id == created.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsBlankName(string blankName)
    {
        var service = NewService();

        Assert.Throws<ArgumentException>(() => service.Create(new NewWidget(blankName, 1)));
    }

    [Fact]
    public void Delete_RemovesExistingWidget()
    {
        var service = NewService();
        var created = service.Create(new NewWidget("Nut", 5));

        var removed = service.Delete(created.Id);

        Assert.True(removed);
        Assert.DoesNotContain(service.List(), w => w.Id == created.Id);
    }

    [Fact]
    public void Delete_ReturnsFalseForMissingWidget()
    {
        var service = NewService();

        Assert.False(service.Delete(999));
    }
}
