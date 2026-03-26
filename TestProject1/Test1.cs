using jjerhub.TPBookmarks;

namespace jjerhub.TPBookmarks.TestProject1
{
    [TestClass]
    public sealed class Test1
    {
        [TestMethod]
        public void TestMethod1()
        {
            var state = new PatchState()
            {
                //Args = new string[] { "tp", "test" }
                //Args = new string[] { "tp", "upd", "test", "test2" }
                //Args = new string[] { "tp", "rn", "test me", "test3" }
                Args = new string[] { "tp", "add", "test", "test2" }
                //Args = new string[] { "tp", "aka", "alj", "aljx" }
                //Args = new string[] { "tp", "aka", "test2", "test" }
            };
            var result = "unable to teleport";

            TeleportCommandPatch.BeginTeleport(ref state, state.Args);
            TeleportCommandPatch.EndTeleport(ref state, ref result);

            Console.WriteLine(result);
        }

        [TestMethod]
        public void TestMethod2()
        {
            TPAdditions additions = new TPAdditions();
            additions.Add(new TPAddition()
            {
                _Keyword = "alj"
            });
            additions.Add(new TPAddition()
            {
                _Keyword = "alj"
            });

            Assert.HasCount(1, additions);
        }

        [TestMethod]
        public void TestMethod3()
        {
            TPAdditions additions = new TPAdditions();
            additions.Add(new TPAddition()
            {
                _Keyword = "alj"
            });
            additions.Add(new TPAddition()
            {
                _Keyword = "aljx"
            });

            Assert.HasCount(2, additions);
        }
    }
}