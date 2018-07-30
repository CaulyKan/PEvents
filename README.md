# PEvents
A light weight library for full lifecycle async event support

## Getting Started
### 1. Defining a new event

```
public class SomeEvent : PEvent<SomeEvent> { }  //the type parameter for PEvent<> must be equal to your event class
```

### 2. Event lifecycle

The event lifecycle can be included with: new -> prepare -> execute -> success/error -> complete.
The execute phase is running on a different thread. The thread of 'success', 'error' and 'complete' phase depends on how you init the PEventManater(see below)
The event is 'new' when you new the event object, then you need to trigger it to push it into 'prepare' status.

```
var some_event = new SomeEvent();
some_event.Trigger();
```

### 3. Handling lifecycle event

There are three ways to handle the lifecycle event (prepare, execute, success, error, and complete)

3.1 Local handler

Local handler is used when you have access to the event object.
```
var some_event = new SomeEvent();
some_event.OnFinish += e => MessageBox.Show("Finish.");
some_event.Trigger();
```

3.2 Static handler

Static handler is used when you want to handle the event once and only once.

```
[HandlerClass]                                   
public class SomeStaticHandlers					 // Class name does not matter
{
	[HandlePrepareEvent(typeof(SomeEvent))]      
	public static PrepareSomeEvent() { }         // Method name does not matter, but the method must be static
}
```

3.3 Dynamic handler

Dynamic handler is easy to attach or detach an method to an event.

```
public class Test
{
	public Test()
	{
		PEventManager.Instance.HandleFinishEvent<SomeEvent>(e => MessageBox.Show("Finish."));
	}
}
```

### 4. Init PEventManager

PEvents is managed by a PEventManager instance. each manager have its own handler states and threads.
If you are working on WinForm or WPF, and you hope your 'success', 'error' and 'complete' phases fired on your main thread, you should pass an control/window(typically the mian window) to PEventManager;
Otherwise the 'success', 'error' and 'complete' phases are running on the same thread as 'execute' phase.

```
var form = new MainForm();
PEventManager.Instance = new PEventManager(form);
Application.Run(form);
```


### 5. Interupt an event

You can use PEventCancelException to interupt the 'prepare' and 'execute' phase.