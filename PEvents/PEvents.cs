using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Reflection;

namespace PEvents
{
    /*
     * ===========
     * 全局事件系统
     * ===========
     * 
     * 一套在整个应用程序范围内触发事件和获取数据的机制。
     * 
     * 1. 事件系统
     * ==========
     * 1.1. 定义一个事件：
     *      public class SomeEvent : PEvent<SomeEvent> { }  //其中PEvent<SomeEvent>中的泛型类应与类名一致
     * 
     * 1.2. 事件组成
     *      一个事件又4个阶段组成，分别为Prepare -> Execute -> OnFail/OnSuccess -> OnFinish
     *      其中Execute是在其他线程上执行的。
     * 
     * 1.3. 触发一个事件
     *      var some_event = new SomeEvent();
     *      some_event.Trigger();
     * 
     * 1.4. 处理事件
     *      处理一个事件有本地、静态和动态三种写法。
     * 
     * 1.4.1. 本地处理函数
     *      本地处理函数用于在可以直接获得事件实例的情况下使用，例如事件完成时增加回调函数：
     *      var some_event = new SomeEvent();
     *      some_event.OnFinish += e => MessageBox.Show("Finish.");
     *      some_event.Trigger();
     * 
     * 1.4.2 静态处理函数
     *      静态处理函数用于对特定事件执行且仅执行一次的情况下使用。
     *      [HandlerClass]                                   //必须为类加上此特性。
     *      public class SomeStaticHandlers
     *      {
     *          [HandlePrepareEvent(typeof(SomeEvent))]      //处理SomeEvent的Prepare事件
     *          public static PrepareSomeEvent() { }         //函数名无所谓
     *      }
     *     
     * 1.4.3 动态处理函数
     *      动态处理函数需要手动与特定事件绑定，可以实现为多个对象绑定同一函数。
     *      public class Test
     *      {
     *          public Test()
     *          {
     *              PEventManager.Instance.HandleFinishEvent<SomeEvent>(e => MessageBox.Show("Finish."));
     *          }
     *      }
     *      
     * 1.5. 初始化PEventManager
     *      PEventManager是全局事件管理器，必须被首先初始化。建议在main()函数中完成此步骤。
     *      如果是在WinForm环境下使用PEventManager，为使得Fail,Success和Finish可以在主线程上执行，建议以启动窗体作为其构造参数。
     *      var form = new MainForm();
     *      PEventManager.Instance = new PEventManager(form);
     *      Application.Run(form);
     *      
     * 1.6. 取消事件
     *      在事件执行的过程中，可以使用PEventCancelException中断事件Prepare和Execute的执行，并根据指定的参数调用Success/Fail
     *      
     * 1.7. 注意
     *      PEventManager在处理动态函数时，使用WeakReference保持对象。当回调函数的对象被回收后，该函数将被自动剔除。
     * 
     * 
     * 
     */


    /// <summary>
    /// 所有全局事件的基类。
    /// </summary>
    public abstract class PEvent<TEvent> where TEvent: PEvent<TEvent>
    {
        public event PEventManager.PEventHandler<TEvent> Prepare;

        public event PEventManager.PEventHandler<TEvent> Execute;

        public event PEventManager.NuErrorEventHandler<TEvent> OnError;

        public event PEventManager.PEventHandler<TEvent> OnSuccess;

        public event PEventManager.PEventHandler<TEvent> OnFinish;

        private Task task;
        private CancellationTokenSource token;

        public bool Success
        {
            get;
            private set;
        }

        /// <summary>
        /// 强制中断一个处于异步执行中的事件。
        /// </summary>
        public void Abort()
        {
            if (this.task == null)
                throw new InvalidOperationException("该事件并处于Execute阶段，因此无法取消。");

            token.Cancel();
        }

        /// <summary>
        /// 等待一个异步中的事件执行完成。
        /// </summary>
        public void Wait()
        {
            if (this.task == null)
                throw new InvalidOperationException("该事件并处于Execute阶段，因此无法等待。");

            this.task.Wait();
        }

        /// <summary>
        /// 触发该全局事件。
        /// </summary>
        public virtual void Trigger()
        {
            if (this.task != null)
                throw new InvalidOperationException("在该事件的上一个Execute任务未完成时，无法发起一个新的事件。");

            PEventManager.Instance.SetupEventHandlers(this);

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

                task = new Task((t) =>
                    {
                        this.Execute(this as TEvent);
                    }, token);

                task.ContinueWith(TaskComplete);
                task.Start();
            }
            else
            {
                TaskComplete(null);
            }
        }
        
        internal void TaskFail(Exception e)
        {
            try
            {
                if (e is PEventCancelException && (e as PEventCancelException).Success == true)
                {
                    this.Success = true;
                    if (this.OnSuccess != null)
                    {
                        this.OnSuccess(this as TEvent);
                    }
                }
                else
                {
                    this.Success = false;
                    if (this.OnError != null)
                    {
                        this.OnError(this as TEvent, e);
                    }
                }
            }
            finally
            {
                if (this.OnFinish != null)
                    this.OnFinish(this as TEvent);
            }
        }

        internal void TaskComplete(Task t)
        {
#if !NETSTANDARD2_0
            var ctrl = PEventManager.Instance.MainThreadControl as System.Windows.Forms.Control;
            if (ctrl != null && ctrl.InvokeRequired)
            {
                ctrl.Invoke(new Action<Task>(TaskComplete), new object[] { t });
            }
            else
            {
#endif
                try
                {
                    if (this.task != null && this.task.IsFaulted &&
                        !(this.task.Exception.GetBaseException() is PEventCancelException && !(this.task.Exception.GetBaseException() as PEventCancelException).Success))
                    {
                        this.Success = false;
                        if (this.OnError != null)
                        {
                            this.OnError(this as TEvent, this.task.Exception.GetBaseException());
                        }
                    }
                    else
                    {
                        this.Success = true;
                        if (this.OnSuccess != null)
                        {
                            this.OnSuccess(this as TEvent);
                        }
                    }
                }
                finally
                {
                    this.task = null;

                    if (this.OnFinish != null)
                        this.OnFinish(this as TEvent);
                }
#if !NETSTANDARD2_0
            }
#endif
        }
    }

    /// <summary>
    /// 所有数据传递对象基类
    /// </summary>
    /// <typeparam name="TMessage"></typeparam>
    public abstract class NuMessage<TMessage, TResult> where TMessage : NuMessage<TMessage, TResult>
    {
        public event PEventManager.NuMessageHandler<TMessage, TResult> SyncRefreshData;
        public event PEventManager.NuMessageHandler<TMessage, TResult> AsyncRefreshData;
        public event PEventManager.NuMessageResultHandler<TResult> DataRefreshed;

        public TResult Result
        {
            get;
            set;
        }

        private Task task = null;
        private object locker = new object();
        private CancellationTokenSource token;

        public NuMessage() { }

        public NuMessage(TResult init_value)
        {
            this.Result = init_value;
        }

        public TResult Request()
        {
            if (this.task != null)
                throw new InvalidOperationException("在该消息的上一个AsyncRefreshData任务未完成时，无法发起一个新的请求。");

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
                throw new InvalidOperationException("在该消息的上一个AsyncRefreshData任务未完成时，无法发起一个新的请求。");

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

    /// <summary>
    /// 引发该异常可以以指定的Success或Fail的结果提前中断事件执行。
    /// </summary>
    public class PEventCancelException : Exception 
    {
        public bool Success { get; set; }
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

    /// <summary>
    /// 全局事件管理器
    /// </summary>
    public class PEventManager
    {
        //private Dictionary<Type, List<WeakReference>> prepare_handler = new Dictionary<Type, List<WeakReference>>();
        //private Dictionary<Type, List<WeakReference>> execute_handler = new Dictionary<Type, List<WeakReference>>();
        //private Dictionary<Type, List<WeakReference>> onerror_handler = new Dictionary<Type, List<WeakReference>>();
        //private Dictionary<Type, List<WeakReference>> onsuccess_handler = new Dictionary<Type, List<WeakReference>>();
        //private Dictionary<Type, List<WeakReference>> onfinish_handler = new Dictionary<Type, List<WeakReference>>();
 
        private Dictionary<DelegateType, Dictionary<Type, List<NuWeakDelegate>>> handlers = new Dictionary<DelegateType,Dictionary<Type,List<NuWeakDelegate>>>()
        {
            { DelegateType.EventPrepare, new Dictionary<Type, List<NuWeakDelegate>>() },
            { DelegateType.EventExecute, new Dictionary<Type, List<NuWeakDelegate>>() },
            { DelegateType.EventOnError, new Dictionary<Type, List<NuWeakDelegate>>() },
            { DelegateType.EventOnSuccess, new Dictionary<Type, List<NuWeakDelegate>>() },
            { DelegateType.EventOnFinish, new Dictionary<Type, List<NuWeakDelegate>>() },
            { DelegateType.MessageSyncRefreshData, new Dictionary<Type, List<NuWeakDelegate>>() },
            { DelegateType.MessageAsyncRefreshData, new Dictionary<Type, List<NuWeakDelegate>>() },
            { DelegateType.MessageDataRefreshed, new Dictionary<Type, List<NuWeakDelegate>>() },
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
            { DelegateType.EventOnError, typeof(NuErrorEventHandler<>) },
            { DelegateType.EventOnSuccess, typeof(PEventHandler<>) },
            { DelegateType.EventOnFinish, typeof(PEventHandler<>) },
        };

        /// <summary>
        /// 单例模式。PEventManager的子类必须覆盖此方法。
        /// </summary>
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

#if !NETSTANDARD2_0
        public PEventManager(Control main_thread_control) : this()
        {
            this.MainThreadControl = main_thread_control;
        }
#endif

        public delegate void PEventHandler<T>(T args) where T : PEvent<T>;
        public delegate void NuErrorEventHandler<T>(T args, Exception ex) where T : PEvent<T>;
        public delegate void NuMessageHandler<TMessage, TResult>(TMessage args) where TMessage : NuMessage<TMessage, TResult>;
        public delegate void NuMessageResultHandler<TResult>(TResult result, Exception ex);

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
                handlers[dt][t].Add(new NuWeakDelegate(handler));
            }
            else
            {
                handlers[dt].Add(t, new List<NuWeakDelegate>() { new NuWeakDelegate(handler) });
            }
        }
        
        private void RemoveDeadHandlers()
        {
            foreach (var handler_group in handlers.Values)
            {
                handler_group.Values.ToList().ForEach(i => i.RemoveAll(j => !j.IsStatic && !j.Object.IsAlive));
            }
        }

        /// <summary>
        /// 为指定事件添加处理器。
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="handler"></param>
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

        public void HandleErrorEvent<T>(NuErrorEventHandler<T> handler)
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

        public void HandleSyncRefreshMessage<TMessage, TResult>(NuMessageHandler<TMessage, TResult> handler)
            where TMessage : NuMessage<TMessage, TResult>
        {
            AddEventHandler<TMessage>(handler, DelegateType.MessageSyncRefreshData);
        }

        public void HandleAsyncRefreshMessage<TMessage, TResult>(NuMessageHandler<TMessage, TResult> handler)
            where TMessage : NuMessage<TMessage, TResult>
        {
            AddEventHandler<TMessage>(handler, DelegateType.MessageAsyncRefreshData);
        }

        public void HandleDataRefreshedMessage<TMessage, TResult>(NuMessageResultHandler<TResult> handler)
            where TMessage : NuMessage<TMessage, TResult>
        {
            AddEventHandler<TMessage>(handler, DelegateType.MessageDataRefreshed);
        }

        public void SetupEventHandlers<T>(PEvent<T> e) 
            where T : PEvent<T>
        {
            //删除已经死亡的引用
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
                    //if (i.Target != null)
                    //    e.Execute += i.Target as PEventHandler<T>;
                    e.Execute += System.Delegate.CreateDelegate(typeof(PEventHandler<T>), i.IsStatic ? null : i.Object.Target, i.Method) as PEventHandler<T>;
                });
            }

            if (this.handlers[DelegateType.EventOnError].ContainsKey(typeof(T)))
            {
                this.handlers[DelegateType.EventOnError][typeof(T)].ForEach(i =>
                {
                    //if (i.Target != null)
                    //    e.OnError += i.Target as NuErrorEventHandler<T>;
                    e.OnError += System.Delegate.CreateDelegate(typeof(NuErrorEventHandler<T>), i.IsStatic ? null : i.Object.Target, i.Method) as NuErrorEventHandler<T>;
                });
            }

            if (this.handlers[DelegateType.EventOnSuccess].ContainsKey(typeof(T)))
            {
                this.handlers[DelegateType.EventOnSuccess][typeof(T)].ForEach(i =>
                {
                    //if (i.Target != null)
                    //    e.OnSuccess += i.Target as PEventHandler<T>;
                    e.OnSuccess += System.Delegate.CreateDelegate(typeof(PEventHandler<T>), i.IsStatic ? null : i.Object.Target, i.Method) as PEventHandler<T>;
                });
            }

            if (this.handlers[DelegateType.EventOnFinish].ContainsKey(typeof(T)))
            {
                this.handlers[DelegateType.EventOnFinish][typeof(T)].ForEach(i =>
                {
                    //if (i.Target != null)
                    //    e.OnFinish += i.Target as PEventHandler<T>;
                    e.OnFinish += System.Delegate.CreateDelegate(typeof(PEventHandler<T>), i.IsStatic ? null : i.Object.Target, i.Method) as PEventHandler<T>;
                });
            }
            
        }
        
        public void SetupMessageHandlers<TMessage, TResult>(NuMessage<TMessage, TResult> m) 
            where TMessage : NuMessage<TMessage, TResult>
        {
            //删除已经死亡的引用
            RemoveDeadHandlers();

            if (this.handlers[DelegateType.MessageAsyncRefreshData].ContainsKey(typeof(TMessage)))
            {
                this.handlers[DelegateType.MessageAsyncRefreshData][typeof(TMessage)].ForEach(i =>
                {
                    m.AsyncRefreshData += System.Delegate.CreateDelegate(typeof(NuMessageHandler<TMessage, TResult>), i.IsStatic ? null : i.Object.Target, i.Method) as NuMessageHandler<TMessage, TResult>;
                });
            }

            if (this.handlers[DelegateType.MessageSyncRefreshData].ContainsKey(typeof(TMessage)))
            {
                this.handlers[DelegateType.MessageSyncRefreshData][typeof(TMessage)].ForEach(i =>
                {
                    m.SyncRefreshData += System.Delegate.CreateDelegate(typeof(NuMessageHandler<TMessage, TResult>), i.IsStatic ? null : i.Object.Target, i.Method) as NuMessageHandler<TMessage, TResult>;
                });
            }

            if (this.handlers[DelegateType.MessageDataRefreshed].ContainsKey(typeof(TMessage)))
            {
                this.handlers[DelegateType.MessageDataRefreshed][typeof(TMessage)].ForEach(i =>
                {
                    m.DataRefreshed += System.Delegate.CreateDelegate(typeof(NuMessageResultHandler<TResult>), i.IsStatic ? null : i.Object.Target, i.Method) as NuMessageResultHandler<TResult>;
                });
            }
        }

        private class NuWeakDelegate
        {
            public NuWeakDelegate(Delegate d)
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
                throw new InvalidCastException("HandlerClass必须指定一个PEventManager的子类，或为空。");
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
