using FluentAssertions;
using Modular.Core.Deployment;
using Xunit;

namespace Modular.Core.Tests.Deployment;

/// <summary>
/// <see cref="OrderPlacement.Apply{T}"/>: the one function every order change goes through.
/// </summary>
public sealed class OrderPlacementTests
{
    private static readonly string[] List = ["a", "b", "c", "d", "e"];

    [Theory]
    [InlineData("c", "top", "c,a,b,d,e")]
    [InlineData("c", "bottom", "a,b,d,e,c")]
    [InlineData("a", "bottom", "b,c,d,e,a")]
    [InlineData("e", "top", "e,a,b,c,d")]
    public void TopAndBottom(string moving, string where, string expected)
    {
        var placement = where == "top" ? OrderPlacement.Top : OrderPlacement.Bottom;

        Apply(placement, moving).Should().Equal(expected.Split(','));
    }

    [Fact]
    public void Before_PutsItDirectlyAboveTheAnchor()
    {
        Apply(OrderPlacement.Before("b"), "e").Should().Equal("a", "e", "b", "c", "d");
    }

    [Fact]
    public void After_PutsItDirectlyBelowTheAnchor_SoItWinsOverIt()
    {
        Apply(OrderPlacement.After("d"), "a").Should().Equal("b", "c", "d", "a", "e");
    }

    [Fact]
    public void AnchorsAreMatchedWhateverTheirCase()
    {
        Apply(OrderPlacement.After("D"), "A").Should().Equal("b", "c", "d", "a", "e");
    }

    [Theory]
    [InlineData(1, "c,a,b,d,e")]
    [InlineData(3, "a,b,c,d,e")]
    [InlineData(5, "a,b,d,e,c")]
    public void At_CountsInTheFinishedList(int position, string expected)
    {
        Apply(OrderPlacement.At(position), "c").Should().Equal(expected.Split(','));
    }

    [Theory]
    [InlineData(0, "c,a,b,d,e")]
    [InlineData(99, "a,b,d,e,c")]
    public void At_APositionOffTheEnd_IsClampedToIt(int position, string expected)
    {
        Apply(OrderPlacement.At(position), "c").Should().Equal(expected.Split(','));
    }

    [Fact]
    public void ABlock_MovesTogether_InTheOrderItHadInTheList()
    {
        // Given out of order on purpose: the list's own order is what is kept.
        Apply(OrderPlacement.Bottom, "d", "b").Should().Equal("a", "c", "e", "b", "d");
        Apply(OrderPlacement.After("e"), "d", "a").Should().Equal("b", "c", "e", "a", "d");
    }

    [Fact]
    public void TheInputListIsNotChanged()
    {
        var list = List.ToList();

        OrderPlacement.Top.Apply(list, ["e"], x => x);

        list.Should().Equal(List);
    }

    [Fact]
    public void PlacingAnEntryBesideItself_IsRefused()
    {
        var act = () => Apply(OrderPlacement.After("c"), "c");

        act.Should().Throw<ArgumentException>().WithMessage("*beside itself*");
    }

    [Fact]
    public void AnAnchorNotInTheList_IsRefused()
    {
        var act = () => Apply(OrderPlacement.Before("zz"), "c");

        act.Should().Throw<ArgumentException>().WithMessage("*'zz' is not in the list*");
    }

    [Fact]
    public void MovingSomethingNotInTheList_IsRefused()
    {
        var act = () => Apply(OrderPlacement.Top, "zz");

        act.Should().Throw<ArgumentException>().WithMessage("*'zz' is not in the list*");
    }

    [Fact]
    public void MovingNothing_IsRefused()
    {
        var act = () => OrderPlacement.Top.Apply(List, Array.Empty<string>(), x => x);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(PlacementKind.Top, "at the top")]
    [InlineData(PlacementKind.Bottom, "at the bottom")]
    public void Describe_SaysWhereItGoes(PlacementKind kind, string expected)
    {
        new OrderPlacement(kind).Describe().Should().Be(expected);
        OrderPlacement.After("x").Describe("SBBF").Should().Be("after 'SBBF'");
        OrderPlacement.At(3).Describe().Should().Be("at #3");
    }

    private static List<string> Apply(OrderPlacement placement, params string[] moving) =>
        placement.Apply(List, moving, x => x);
}
