using System;
using Xunit;
using PEvents;

namespace PEvents.Tests
{
    public class PEventTests
    {
        PEventManager manager;

        public PEventTests ()
        {
            manager = new PEventManager();
        }

        [Fact]
        public void TestBasic()
        {
            var ev = new TestEvent1();
            ev.Prepare += e => e.prepareCounter++;
            ev.Execute += e => e.executeCounter++;
            ev.Success += e => e.successCounter++;
            ev.Error += (e, ex) => e.errorCounter++;
            ev.Complete += e => e.completeCounter++;
            ev.Trigger(manager);

            ev.Wait();

            Assert.Equal(2, ev.prepareCounter);
            Assert.Equal(2, ev.executeCounter);
            Assert.Equal(2, ev.successCounter);
            Assert.Equal(0, ev.errorCounter);
            Assert.Equal(2, ev.completeCounter);
        }
    }

    class TestEvent1 : PEvent<TestEvent1>
    {
        public int prepareCounter = 0, executeCounter = 0, successCounter = 0, errorCounter = 0, completeCounter = 0;

        public override void OnPrepare(TestEvent1 e)
        {
            prepareCounter = 1;
        }

        public override void OnExecute(TestEvent1 e)
        {
            executeCounter = 1;
        }

        public override void OnSuccess(TestEvent1 e)
        {
            successCounter = 1;
        }

        public override void OnError(TestEvent1 e, Exception ex)
        {
            errorCounter = 1;
        }

        public override void OnComplete(TestEvent1 e)
        {
            completeCounter = 1;
        }
    }
}
