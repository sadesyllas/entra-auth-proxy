namespace EntraInjectorProxy;

public class TokenProvider
{
    private string _token = string.Empty;

    public string GetToken() => Volatile.Read(ref _token);

    public void SetToken(string token)
    {
        if (token != null)
        {
            Volatile.Write(ref _token, token);
        }
    }
}