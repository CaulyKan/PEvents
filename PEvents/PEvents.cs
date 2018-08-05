#if !NETSTANDARD2_0
    #define WPF
#endif

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Reflection;
using System.ComponentModel;

namespace PEvents
{
    public abstract class PEvent<TEvent> where TEvent: PEvent<TEvent>
    {
        public event PEventManager.PEventHandler<TEvent> Prepare;

        public event PEventManager.PEventHandler<TEvent> Execute;

        public event PEventManager.PEventErrorEventHandler<TEvent> Error;

        public event PEventManager.PEventHandler<TEvent> Success;

        public event PEventManager.PEventHandler<TEvent> Complete;

        private Task task;
        private Task completeTask;
        private CancellationTokenSource token;

        public PEvent()
        {
            this.Prepare += this.OnPrepare;
            this.Execute += this.OnExecute;
            this.Error += this.OnError;
            this.Success += this.OnSuccess;
            this.Complete += this.OnComplete;
        }

        public virtual void OnPrepare(TEvent e) { }
        public virtual void OnExecute(TEvent e) { }
        public virtual void OnError(TEvent e, Exception ex) { }
        public virtual void OnSuccess(TEvent e) { }
        public virtual void OnComplete(TEvent e) { }

        public bool IsSuccess
        {
            get;
            private set;
        }

        public void Abort()
        {
            if (this.completeTask == null)
                throw new InvalidOperationException("This event is not triggered, so it can't be aborted.");

            token.Cancel();
        }
        
        public void Wait()
        {
            if (this.completeTask == null)
                throw new InvalidOperationException("This event is not triggered, so it can't be waited.");

            this.completeTask.Wait();
        }
        
        public virtual void Trigger(PEventManager manager)
        {
            if (this.task != null)
                throw new InvalidOperationException("This event can't be triggered before the previous lifecycle is finished.");

            manager.SetupEventHandlers(this);

            if (this.Prepare != null)
            {
                try
                {
                    Prepare(this as TEvent);
                }
                catch (Exception e)
                {
                    TaskFail(e);

                    return;
                }
            }

            if (this.Execute != null)
            {
                token = new CancellationTokenSource();

                task = new Task((t) => this.Execute(this as TEvent), token);

                completeTask = task.ContinueWith(task => TaskComplete(task, manager));

                task.Start();
            }
            else
            {
                TaskComplete(null, manager);
            }
        }
        
        internal void TaskFail(Exception e)
        {
            try
            {
                if (e is PEventCancelException && (e as PEventCancelException).IgnoreSuccess) return;
                else if (e is PEventCancelException && (e as PEventCancelException).Success)
                {
                    this.IsSuccess = true;
                    if (this.Success != null)
                    {
                        this.Success(this as TEvent);
                    }
                }
                else
                {
                    this.IsSuccess = false;
                    if (this.Error != null)
                    {
                        this.Error(this as TEvent, e);
                    }
                }
            }
            finally
            {
                if (this.Complete != null)
                    this.Complete(this as TEvent);
            }
        }

        internal void TaskComplete(Task t, PEventManager manager)
        {
            var ctrl = manager.MainThreadControl;
            
            if (ctrl != null && ctrl is ISynchronizeInvoke && (ctrl as ISynchronizeInvoke).InvokeRequired)
            {
                (ctrl as ISynchronizeInvoke).Invoke(new Action<Task>(task => TaskComplete(task, manager)), new object[] { t, manager });
            }
#if WPF
            else if (ctrl != null && ctrl is System.Windows.Threading.DispatcherObject && !(ctrl as System.Windows.Threading.DispatcherObject).Dispatcher.CheckAccess())
            {
                (ctrl as System.Windows.Threading.DispatcherObject).Dispatcher.Invoke(new Action<Task>(task => TaskComplete(task, manager)), new object[] { t, manager });
            }
#endif
            else
            {
                try
                {
                    this.IsSuccess = true;

                    if (this.task != null && this.task.IsFaulted)
                    {
                        var exception = this.task.Exception.GetBaseException();

                        if (exception is PEventCancelException) 
                        {
                            var canelException = exception as PEventCancelException;

                            if (!canelException.IgnoreSuccess) 
                            {
                                if (!canelException.Success) this.IsSuccess = false;
                            }
                            else return;
                        }
                        else this.IsSuccess = false;
                    }

                    if (this.IsSuccess) 
                    {
                        if (this.Success != null) this.Success(this as TEvent);
                    }
                    else 
                    {
                        if (this.Error != null) this.Error(this as TEvent, this.task.Exception.GetBaseException());
                    }
                }
                finally
                {
                    this.task = null;
                    this.completeTask = null;

                    if (this.Complete != null)
                        this.Complete(this as TEvent);
                }
            }
        }
    }
    
    public abstract class PMessage<TMessage, TResult> where TMessage : PMessage<TMessage, TResult>
    {
        public event PEventManager.PMessageHandler<TMessage, TResult> SyncRefreshData;
        public event PEventManager.PMessageHandler<TMessage, TResult> AsyncRefreshData;
        public event PEventManager.PMessageResultHandler<TResult> DataRefreshed;

        public TResult Result
        {
            get;
            set;
        }

        private Task task = null;
        private object locker = new object();
        private CancellationTokenSource token;

        public PMessage() { }

        public PMessage(TResult init_value) : this()
        {
            this.Result = init_value;
        }

        public TResult Request()
        {
            if (this.task != null)
                throw new InvalidOperationException("This message can't be requested before the previous lifecycle is finished.");

            lock (this.locker)
            {
                PEventManager.Instance.SetupMessageHandlers(this);

                if (this.AsyncRefreshData != null)
                {
                    token = new CancellationTokenSource();

                    task = new Task(t =>
                        {
                            this.AsyncRefreshData(this as TMessage);
                        }, token);

                    task.Start();
                }

                if (this.SyncRefreshData != null)
                {
                    try
                    {
                        this.SyncRefreshData(this as TMessage);
                    }
                    finally
                    {
                        if (task != null && task.Status == TaskStatus.Running)
                            token.Cancel();
                    }
                }

                if (task != null && task.Status == TaskStatus.Running)
                {
                    try
                    {
                    task.Wait();
                    }
                    catch
                    {
                        throw;
                    }
                }

                return this.Result;
            }
        }

        public void RequestAsync()
        {
            if (this.task != null)
                throw new InvalidOperationException("This message can't be requested before the previous lifecycle is finished.");

            lock (this.locker)
            {
                PEventManager.Instance.SetupMessageHandlers(this);

                if (this.AsyncRefreshData != null)
                {
                    token = new CancellationTokenSource();

                    task = new Task(t =>
                        {
                            this.AsyncRefreshData(this as TMessage);
                        }, token);

                    task.ContinueWith(TaskComplete);

                    task.Start();
                }

                if (this.SyncRefreshData != null)
                {
                    try
                    {
                        this.SyncRefreshData(this as TMessage);
                    }
                    finally
                    {
                        if (task != null && task.Status == TaskStatus.Running)
                            token.Cancel();
                    }
                }
            }
        }
        
        internal void TaskComplete(Task t)
        {
            try
            {
                if (this.DataRefreshed != null)
                {
                    this.DataRefreshed(this.Result, t.Exception);
                }
            }
            finally
            {
                this.task = null;
            }
        }
    }
    
    public class PEventCancelException : Exception 
    {
        public bool Success { get; set; }
        public bool IgnoreSuccess { get; set; } = false;

        public PEventCancelException() : base()
        {
            this.IgnoreSuccess = true;
        }

        public PEventCancelException(bool success ) : base()
        {
            this.Success = success;
        }

        public PEventCancelException(bool success, string msg) : base(msg) 
        {
            this.Success = success;
        }

        public PEventCancelException(bool success, string msg, Exception inner_exception) : base(msg, inner_exception)
        {
            this.Success = success;
        }
    }
    
    public class PEventManager
    {
        private Dictionary<DelegateType, Dictionary<Type, List<PEventWeakDelegate>>> handlers = new Dictionary<DelegateType,Dictionary<Type,List<PEventWeakDelegate>>>()
        {
            { DelegateType.EventPrepare, new Dictionary<Type, List<PEventWeakDelegate>>() },
            { DelegateType.EventExecute, new Dictionary<Type, List<PEventWeakDelegate>>() },
            { DelegateType.EventOnError, new Dictionary<Type, List<PEventWeakDelegate>>() },
            { DelegateType.EventOnSuccess, new Dictionary<Type, List<PEventWeakDelegate>>() },
            { DelegateType.EventOnFinish, new Dictionary<Type, List<PEventWeakDelegate>>() },
            { DelegateType.MessageSyncRefreshData, new Dictionary<Type, List<PEventWeakDelegate>>() },
            { DelegateType.MessageAsyncRefreshData, new Dictionary<Type, List<PEventWeakDelegate>>() },
            { DelegateType.MessageDataRefreshed, new Dictionary<Type, List<PEventWeakDelegate>>() },
        };

        public enum DelegateType
        {
            EventPrepare,
            EventExecute,
            EventOnError,
            EventOnSuccess,
            EventOnFinish,
            MessageSyncRefreshData,
            MessageAsyncRefreshData,
            MessageDataRefreshed,
        }
        
        private Dictionary<DelegateType, Type> DelegateTypeMap = new Dictionary<DelegateType,Type>()
        {
            { DelegateType.EventPrepare, typeof(PEventHandler<>) },
            { DelegateType.EventExecute, typeof(PEventHandler<>) },
            { DelegateType.EventOnError, typeof(PEventErrorEventHandler<>) },
            { DelegateType.EventOnSuccess, typeof(PEventHandler<>) },
            { DelegateType.EventOnFinish, typeof(PEventHandler<>) },
        };
        
        public static PEventManager Instance
        {
            get;
            set;
        }

        public object MainThreadControl
        {
            get;
            private set;
        }

        public PEventManager()
        {
            SetupStaticEventHandlers();
        }
        
        public PEventManager(object main_thread_control) : this()
        {
            this.MainThreadControl = main_thread_control;
        }

        public delegate void PEventHandler<T>(T args) where T : PEvent<T>;
        public delegate void PEventErrorEventHandler<T>(T args, Exception ex) where T : PEvent<T>;
        public delegate void PMessageHandler<TMessage, TResult>(TMessage args) where TMessage : PMessage<TMessage, TResult>;
        public delegate void PMessageResultHandler<TResult>(TResult result, Exception ex);

        public void Trigger<TEvent>(TEvent pEvent) where TEvent: PEvent<TEvent>
        {
            pEvent.Trigger(this);
        }

        private void SetupStaticEventHandlers()
        {
            var assembs = AppDomain.CurrentDomain.GetAssemblies();

            foreach (Assembly assemb in assembs)
            {
                foreach (Type t in assemb.GetTypes())
                {
                    foreach (Attribute attr in t.GetCustomAttributes(typeof(HandlerClassAttribute), true))
                    {
                        var cattr = attr as HandlerClassAttribute;

                        if (cattr.EventManagerType == null || cattr.EventManagerType == this.GetType())
                        {
                            foreach (MethodInfo method in t.GetMethods())
                            {
                                foreach (Attribute attr2 in method.GetCustomAttributes(typeof(HandlerAttribute), true))
                                {
                                    var mattr = attr2 as HandlerAttribute;

                                    var handler_type = this.DelegateTypeMap[mattr.DelegateType].MakeGenericType(new Type[] { mattr.TargetType });

                                    var handler = System.Delegate.CreateDelegate(handler_type, method);

                                    AddEventHandler(mattr.TargetType, handler, mattr.DelegateType);
                                }
                            }
                        }
                    }
                }
            }
        }

        private void AddEventHandler<T>(System.Delegate handler, DelegateType dt)
        {
            AddEventHandler(typeof(T), handler, dt);
        }
        
        private void AddEventHandler(Type t, System.Delegate handler, DelegateType dt)
        {
            if (handlers[dt].ContainsKey(t))
            {
                handlers[dt][t].Add(new PEventWeakDelegate(handler));
            }
            else
            {
                handlers[dt].Add(t, new List<PEventWeakDelegate>() { new PEventWeakDelegate(handler) });
            }
        }
        
        private void RemoveDeadHandlers()
        {
            foreach (var handler_group in handlers.Values)
            {
                handler_group.Values.ToList().ForEach(i => i.RemoveAll(j => !j.IsStatic && !j.Object.IsAlive));
            }
        }
        
        public void HandlePrepareEvent<T>(PEventHandler<T> handler)
            where T:PEvent<T>
        {
            AddEventHandler<T>(handler, DelegateType.EventPrepare);
        }

        public void HandleExecuteEvent<T>(PEventHandler<T> handler)
            where T : PEvent<T>
        {
            AddEventHandler<T>(handler, DelegateType.EventExecute);
        }

        public void HandleErrorEvent<T>(PEventErrorEventHandler<T> handler)
            where T : PEvent<T>
        {
            AddEventHandler<T>(handler, DelegateType.EventOnError);
        }

        public void HandleSuccessEvent<T>(PEventHandler<T> handler)
            where T : PEvent<T>
        {
            AddEventHandler<T>(handler, DelegateType.EventOnSuccess);
        }

        public void HandleFinishEvent<T>(PEventHandler<T> handler)
            where T : PEvent<T>
        {
            AddEventHandler<T>(handler, DelegateType.EventOnFinish);
        }

        public void HandleSyncRefreshMessage<TMessage, TResult>(PMessageHandler<TMessage, TResult> handler)
            where TMessage : PMessage<TMessage, TResult>
        {
            AddEventHandler<TMessage>(handler, DelegateType.MessageSyncRefreshData);
        }

        public void HandleAsyncRefreshMessage<TMessage, TResult>(PMessageHandler<TMessage, TResult> handler)
            where TMessage : PMessage<TMessage, TResult>
        {
            AddEventHandler<TMessage>(handler, DelegateType.MessageAsyncRefreshData);
        }

        public void HandleDataRefreshedMessage<TMessage, TResult>(PMessageResultHandler<TResult> handler)
            where TMessage : PMessage<TMessage, TResult>
        {
            AddEventHandler<TMessage>(handler, DelegateType.MessageDataRefreshed);
        }

        public void SetupEventHandlers<T>(PEvent<T> e) 
            where T : PEvent<T>
        {
            RemoveDeadHandlers();

            if (this.handlers[DelegateType.EventPrepare].ContainsKey(typeof(T)))
            {
                this.handlers[DelegateType.EventPrepare][typeof(T)].ForEach(i =>
                {
                    e.Prepare += System.Delegate.CreateDelegate(typeof(PEventHandler<T>), i.IsStatic? null: i.Object.Target, i.Method) as PEventHandler<T>;
                });
            }

            if (this.handlers[DelegateType.EventExecute].ContainsKey(typeof(T)))
            {
                this.handlers[DelegateType.EventExecute][typeof(T)].ForEach(i =>
                {
                    e.Execute += System.Delegate.CreateDelegate(typeof(PEventHandler<T>), i.IsStatic ? null : i.Object.Target, i.Method) as PEventHandler<T>;
                });
            }

            if (this.handlers[DelegateType.EventOnError].ContainsKey(typeof(T)))
            {
                this.handlers[DelegateType.EventOnError][typeof(T)].ForEach(i =>
                {
                    e.Error += System.Delegate.CreateDelegate(typeof(PEventErrorEventHandler<T>), i.IsStatic ? null : i.Object.Target, i.Method) as PEventErrorEventHandler<T>;
                });
            }

            if (this.handlers[DelegateType.EventOnSuccess].ContainsKey(typeof(T)))
            {
                this.handlers[DelegateType.EventOnSuccess][typeof(T)].ForEach(i =>
                {
                    e.Success += System.Delegate.CreateDelegate(typeof(PEventHandler<T>), i.IsStatic ? null : i.Object.Target, i.Method) as PEventHandler<T>;
                });
            }

            if (this.handlers[DelegateType.EventOnFinish].ContainsKey(typeof(T)))
            {
                this.handlers[DelegateType.EventOnFinish][typeof(T)].ForEach(i =>
                {
                    e.Complete += System.Delegate.CreateDelegate(typeof(PEventHandler<T>), i.IsStatic ? null : i.Object.Target, i.Method) as PEventHandler<T>;
                });
            }
            
        }
        
        public void SetupMessageHandlers<TMessage, TResult>(PMessage<TMessage, TResult> m) 
            where TMessage : PMessage<TMessage, TResult>
        {
            RemoveDeadHandlers();

            if (this.handlers[DelegateType.MessageAsyncRefreshData].ContainsKey(typeof(TMessage)))
            {
                this.handlers[DelegateType.MessageAsyncRefreshData][typeof(TMessage)].ForEach(i =>
                {
                    m.AsyncRefreshData += System.Delegate.CreateDelegate(typeof(PMessageHandler<TMessage, TResult>), i.IsStatic ? null : i.Object.Target, i.Method) as PMessageHandler<TMessage, TResult>;
                });
            }

            if (this.handlers[DelegateType.MessageSyncRefreshData].ContainsKey(typeof(TMessage)))
            {
                this.handlers[DelegateType.MessageSyncRefreshData][typeof(TMessage)].ForEach(i =>
                {
                    m.SyncRefreshData += System.Delegate.CreateDelegate(typeof(PMessageHandler<TMessage, TResult>), i.IsStatic ? null : i.Object.Target, i.Method) as PMessageHandler<TMessage, TResult>;
                });
            }

            if (this.handlers[DelegateType.MessageDataRefreshed].ContainsKey(typeof(TMessage)))
            {
                this.handlers[DelegateType.MessageDataRefreshed][typeof(TMessage)].ForEach(i =>
                {
                    m.DataRefreshed += System.Delegate.CreateDelegate(typeof(PMessageResultHandler<TResult>), i.IsStatic ? null : i.Object.Target, i.Method) as PMessageResultHandler<TResult>;
                });
            }
        }

        private class PEventWeakDelegate
        {
            public PEventWeakDelegate(Delegate d)
            {
                if (d.Target != null) this.Object = new WeakReference(d.Target);
                this.Method = d.Method;
            }

            public WeakReference Object;
            public MethodInfo Method;

            public bool IsStatic
            {
                get
                {
                    return this.Object == null;
                }
            }
        }


    }

    public abstract class HandlerAttribute : Attribute
    {
        public virtual PEventManager.DelegateType DelegateType
        {
            get
            {
                throw new NotImplementedException();
            }
        }

        public virtual Type TargetType
        {
            get;
            private set;
        }

        public HandlerAttribute(Type type)
        {
            this.TargetType = type;
        }
    }

    public class HandlerClassAttribute : Attribute
    {
        public Type EventManagerType
        {
            get;
            private set;
        }

        public HandlerClassAttribute() { }

        public HandlerClassAttribute(Type event_manager_type)
        {
            if (event_manager_type != null && !event_manager_type.IsSubclassOf(typeof(PEventManager)))
            {
                throw new InvalidCastException("The event_manager_type param for HandlerClassAttribute must be null or a subclass to PEventmanager.");
            }

            this.EventManagerType = event_manager_type;
        }
    }

    public class HandlePrepareEventAttribute : HandlerAttribute
    {
        public override PEventManager.DelegateType DelegateType
        {
            get
            {
                return PEventManager.DelegateType.EventPrepare;
            }
        }

        public HandlePrepareEventAttribute(Type t) : base(t) { }
    }

    public class HandleExecuteEventAttribute :HandlerAttribute
    {
        public override PEventManager.DelegateType DelegateType
        {
            get
            {
                return PEventManager.DelegateType.EventExecute;
            }
        }

        public HandleExecuteEventAttribute(Type t) : base(t) { }
    }

    public class HandleOnErrorEventAttribute : HandlerAttribute
    {
        public override PEventManager.DelegateType DelegateType
        {
            get
            {
                return PEventManager.DelegateType.EventOnError;
            }
        }

        public HandleOnErrorEventAttribute(Type t) : base(t) { }
    }

    public class HandleOnSuccessEventAttribute : HandlerAttribute
    {
        public override PEventManager.DelegateType DelegateType
        {
            get
            {
                return PEventManager.DelegateType.EventOnSuccess;
            }
        }

        public HandleOnSuccessEventAttribute(Type t) : base(t) { }
    }

    public class HandleOnFinishEventAttribute : HandlerAttribute
    {
        public override PEventManager.DelegateType DelegateType
        {
            get
            {
                return PEventManager.DelegateType.EventOnFinish;
            }
        }

        public HandleOnFinishEventAttribute(Type t) : base(t) { }
    }
}
