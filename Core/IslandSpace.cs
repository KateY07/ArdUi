namespace ArdUi;

// Where the floating island may stand: not under another window's card. FRD's card in the corner of the controlled screen is topmost like the
// island, and whichever is above takes the clicks, so the island steps to the left of it.
static class IslandSpace
{
    public readonly record struct Box(int Left, int Top, int Right, int Bottom);

    // How far (pixels, zero or negative) the pill has to move to the left to clear every card it overlaps.
    public static int Aside(Box pill, IEnumerable<Box> cards)
    {
        var wanted = 0;
        foreach (var card in cards.OrderByDescending(c => c.Left))
        {
            var left = pill.Left + wanted; var right = pill.Right + wanted;
            if (right > card.Left - 6 && left < card.Right && pill.Bottom > card.Top && pill.Top < card.Bottom) wanted -= right - (card.Left - 12);
        }
        return wanted;
    }
}
