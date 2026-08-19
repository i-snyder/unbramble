using UnityEngine;
using static BareHandler;

// NEGATIVE FIXTURE: the guid on the next line sits in a C# comment. .cs files are
// never GUID reference sources, so no edge may ever be extracted from this file.
// guid: 0123456789abcdef0123456789abcdef
//
// Foo inherits BasePawn instead of MonoBehaviour directly, so Level.unity's onLevelPoke event
// (m_MethodName: Poke, m_TargetAssemblyTypeName: Foo, Game) can only match via an inherited-
// member walk -- Foo itself never declares Poke. BasePawn : MonoBehaviour, so Foo remains a
// MonoBehaviour descendant transitively (semantic-mode lifecycle entry-point marking, which
// walks the full base chain, is unaffected; syntactic mode's immediate-base-text check is a
// known limitation).
public class Foo : BasePawn
{
    // A declaration-site field type reference to Bar.cs, referenced nowhere else -- confirms
    // declaration-site type-refs are captured correctly.
    public Bar config;

    // A declaration-site field-type reference to IWeapon.cs, the same mechanism as Bar above --
    // makes the interface live via ordinary propagation once Foo.cs itself is live.
    public IWeapon weapon;

    // Declaration-site field-type references to an enum-only file (GameState.cs) and a
    // delegate-only file (ClickHandler.cs), referenced nowhere else -- same mechanism as
    // Bar/IWeapon above, but exercising the enum/delegate symbol-extraction path specifically
    // (see those files' own comments).
    public GameState state;
    public ClickHandler onClick;

    void Start()
    {
        CoreUtil.Ping();

        // A generic type argument in an invocation -- exercises type-ref extraction for
        // generic arguments.
        AddComponent<Spawned>();

        // A method-group/delegate-conversion reference -- exercises ref extraction for
        // delegate conversions.
        System.Action h = Helper.Handle;
        _ = h;

        // A bare (unqualified) method-group reference reached via `using static BareHandler;`
        // above -- exercises method-group capture for unqualified forms, not just qualified
        // forms like Helper.Handle above.
        System.Action b = HandleBare;
        _ = b;

        // A by-name dispatch literal captured into name_hints as negative evidence
        // (kind='cs-name-literal') -- deliberately shares its target name with an
        // animation-event name-hint elsewhere, so a combined-signal test can exercise both
        // together.
        SendMessage("OnHitFrame");

#if UNITY_ANDROID
        // Identifiers here are never parsed as real syntax under the fixture's empty define
        // set -- captured as name_hints (kind='cs-disabled') instead of vanishing silently.
        var androidOnly = new AndroidOnly();
        androidOnly.DoSomething();

        // Unlike AndroidOnly above, AndroidWorker.cs's entire content is #if UNITY_ANDROID-gated,
        // so it produces zero rows in `symbols` (exercises the zero-symbols screen,
        // ScreenReasons.NoExtractedSymbols) -- see that file's own comment.
        var androidWorker = new AndroidWorker();
        androidWorker.DoWork();

        // A second, mobile-only call site for Jump() -- compiled out under the fixture's empty
        // define set (a desktop input handler and a #if UNITY_ANDROID || UNITY_IPHONE-gated
        // mobile handler both call the same method, but only the desktop call site is visible
        // to a single-define-set semantic analysis). Jump is also invoked for real via the
        // onLocalJump/onLevelPoke UnityEvent bindings in Player.prefab/Level.unity -- who-uses/
        // uses Foo.Jump must show both the real event-bound reference and the
        // disabled-region-refs-possible flag, not just one.
        Jump();
#endif
    }

    public void Jump()
    {
    }
}
