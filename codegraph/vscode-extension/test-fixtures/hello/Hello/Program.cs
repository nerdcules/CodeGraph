namespace Hello;

public static class Program
{
    public static void Main() => System.Console.WriteLine(new Greeter().Greet("world"));
}

public sealed class Greeter
{
    public string Greet(string name) => $"Hello, {name}!";
}
