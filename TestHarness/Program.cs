using System.Threading.Tasks;

namespace TestHarness
{
    class Program
    {
        static void Main(string[] args)
        {
            var t = Task.Delay(10000);

            while (!t.IsCompleted) Task.Delay(1000).Wait();
        }
    }
}
