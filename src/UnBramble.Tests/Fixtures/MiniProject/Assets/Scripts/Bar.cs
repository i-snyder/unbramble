// A plain class referenced only via a declaration-site field type on Foo (`public Bar config;`)
// -- referenced nowhere else. [Serializable] doubles as fixture coverage for the symbols.attrs
// capture used by the attribute screen.
[System.Serializable]
public class Bar
{
}
