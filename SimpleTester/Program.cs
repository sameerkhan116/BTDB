﻿namespace SimpleTester
{
    static class Program
    {
        static void Main(string[] args)
        {
            var btdbTest = new BTDBTest.LowLevelDBTest();
            btdbTest.ValueStoreWorks(4366,0);
        }
    }
}