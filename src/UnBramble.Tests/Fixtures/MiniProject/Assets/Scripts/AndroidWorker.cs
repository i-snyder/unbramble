// Zero-symbols regression fixture: unlike AndroidOnly.cs, whose call site is disabled but whose
// own class body compiles normally under the fixture's empty define set, this file's entire
// content -- including the class declaration itself -- is wrapped in a platform #if that is
// never active under the fixture's (and every test's) empty define set. Roslyn never compiles a
// single token of it, so it produces zero rows in `symbols` -- there is nothing of the
// candidate's own for any liveness screen to match against, so none of them could ever fire.
// It is referenced only from Foo.Start's own #if UNITY_ANDROID-disabled region (`new
// AndroidWorker()`), captured into name_hints (kind='cs-disabled') -- but that capture is
// useless here because the disabled-region screen matches against the candidate's own declared
// symbol names, and this candidate has none. Without a dedicated zero-symbols screen
// (ScreenReasons.NoExtractedSymbols), this file would fall through every screen and be wrongly
// emitted as provenDead despite being referenced from live code.
#if UNITY_ANDROID
using UnityEngine;

public class AndroidWorker : MonoBehaviour
{
    public void DoWork()
    {
    }
}
#endif
