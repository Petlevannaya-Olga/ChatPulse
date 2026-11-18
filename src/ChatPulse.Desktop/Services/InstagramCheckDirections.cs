namespace ChatPulse.Desktop.Services;

[Flags]
public enum InstagramCheckDirections
{
    NONE = 0,
    FOLLOWERS = 1, // Участницы → я (нет среди моих подписчиков)
    FOLLOWING = 2, // Я → участницы (нет в моих подписках)
    BOTH = FOLLOWERS | FOLLOWING
}

public static class InstagramCheckDirectionSelection
{
    public static bool IsValid(InstagramCheckDirections directions) =>
        directions is InstagramCheckDirections.FOLLOWERS or InstagramCheckDirections.FOLLOWING or
            InstagramCheckDirections.BOTH;

    public static IReadOnlyList<string> Filter(IReadOnlyList<string> names,
        InstagramCheckDirections selected, InstagramCheckDirections direction) =>
        (selected & direction) == direction ? names : [];
}
