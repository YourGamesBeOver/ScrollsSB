using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Razer.SwitchbladeSDK2;
using System.Runtime.InteropServices;
using System.Threading;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ScrollsSB.RZSB {
    //some enums that are much cleaner than the RZSBSDK ones
    public enum SBDisplays : int{
        TRACKPAD = 0,
        DK_1,
        DK_2,
        DK_3,
        DK_4,
        DK_5,
        DK_6,
        DK_7,
        DK_8,
        DK_9,
        DK_10,
        NUMBER_OF_DISPLAYS
    }

    public enum SBEvent : int {
        NONE = 0,
        ACTIVATED,
        DEACTIVATED,
        CLOSE,
        EXIT,
        INVALID
    }
    public enum FlickDirection : int {
        NONE = 0,
        LEFT,
        RIGHT,
        UP,
        DONW,
        INVALID
    }


    /************************************
     * SBAPI
     * A wrapper for the Razer Switchblade SDK C# wrapper
     * Yes, I know how stupid that sounds, a wrapper for a wrapper? Why would you ever need that?
     * Well, because Razer sucks at software, that's why.  
     * The default C# 'wrapper' is barely a wrapper at all, it just exposes the C++ functions and exactly copies the structs into C#, 
     * but doesn't actually adapt them to the C# style.
     * 
     * This class provides a much easier, and more useful interface to Razer's RZSBSDKAPI class.  
     * 
     * This class is also thread safe; its functions can be called from any thread!
     * This class also ensures that the SB is stopped in the event of a crash (unhandled exception) or graceful termination
     * 
     * Some jargon and shorthand:
     *  SB - Switchblade
     *  RZ - Razer
     *  RZSBSDKAPI - the class, written by Razer, defined in RZSBSDKWrapper.cs.  It contains all the static functions used to control the SB
     *  DK - Dynamic Key; the 10 keys at the top of the SB
     *  Widget - the trackpad screen (for some reason, Razer occasionally refers to the trackpad screen as the 'Widget')
     *  TP - Trackpad (or the trackpad screen, I use 'TP' to refer to either; You can usually figure out which one from the context)
     *  
     * 
     * 
     ************************************/
    class SBAPI {
        //the number of screens, total, on the SB
        private const int NUMBER_OF_DISPLAYS = (int)SBDisplays.NUMBER_OF_DISPLAYS;
        
        private const UInt32 WM_KEYDOWN = 0x100;
        private const UInt32 WM_KEYUP = 0x101;
        private const UInt32 WM_CHAR = 0x102;

        #region rz constants
        //some constants that Razer thought would be a good idea to keep in enums because they're stupid
        public const int DK_WIDTH = (int)DYNAMICKEY_DISPAY_REGION.SWITCHBLADE_DYNAMIC_KEY_X_SIZE;
        public const int DK_HEIGHT = (int)DYNAMICKEY_DISPAY_REGION.SWITCHBLADE_DYNAMIC_KEY_Y_SIZE;
        public const int DK_IMAGEDATA_SIZE = (int)DYNAMICKEY_DISPAY_REGION.SWITCHBLADE_DK_SIZE_IMAGEDATA;
        public const int DKS_PER_ROW = (int)DYNAMICKEY_DISPAY_REGION.SWITCHBLADE_DYNAMIC_KEYS_PER_ROW;
        public const int DK_ROW_COUNT = (int)DYNAMICKEY_DISPAY_REGION.SWITCHBLADE_DYNAMIC_KEYS_ROWS;

        public const int TP_WIDTH = (int)TOUCHPAD_DISPLAY_REGION.SWITCHBLADE_TOUCHPAD_X_SIZE;
        public const int TP_HEIGHT = (int)TOUCHPAD_DISPLAY_REGION.SWITCHBLADE_TOUCHPAD_Y_SIZE;
        public const int TP_IMAGEDATA_SIZE = (int)TOUCHPAD_DISPLAY_REGION.SWITCHBLADE_TOUCHPAD_SIZE_IMAGEDATA;

        #endregion

        public static bool Started {
            get;
            private set;
        }

        //mutex to ensure that two threads don't access the SB at the same time
        private static Mutex SBMux = new Mutex();



        #region Event and Delegate Declarations
        //*** EVENTS AND DELEGATES ***//

        //private event only used internally
        private static event AppEventCallbackTypeDelegate appEventCallbackDelegate;
        private static event DynamicKeyCallbackDelegate dynamicKeyCallbackDelegate;
        private static event TouchpadGestureCallbackDelegate gestureCallbackDelegate;
        private static event KeyboardCallbackTypeDelegate keyboardCallbackDelegate;

        //public events so other classes can more directly recieve SB callbacks
        public delegate void SimpleDynamicKeyCallbackDelegate(int key, bool down);
        public static event SimpleDynamicKeyCallbackDelegate OnDynamicKeyEvent;
        public delegate void SimpleAppEventCallbackDelegate(SBEvent evnt);//there are also convience delegates below
        public static event SimpleAppEventCallbackDelegate OnAppEvent;
        //public static event TouchpadGestureCallbackDelegate publicTouchpadGestureCallback;
        //To keep things more simple, I have split Touchpad Gestures up into individual callbacks below

        //public events and delegates for each type of TouchpadGesture, seperate
        //events for convience, and so that there are no extraneous parameters
        public delegate void IntXYGestureDelegate(uint touchpoints, ushort xPos, ushort yPos);
        public delegate void XYGestureDelegate(ushort xPos, ushort yPos);
        public delegate void FlickGestureDelegate(uint touches, FlickDirection direction);
        public delegate void ZoomGestureDelegate(bool zoomIn);
        public delegate void RotateGestureDelegate(bool clockwise);
        //called when a finger initially touches the touchpad
        public static event IntXYGestureDelegate OnPressGesture;
        //called when a user removes a finger from the touchpad
        public static event IntXYGestureDelegate OnReleaseGesture;
        //called when a user taps the touchpad
        public static event XYGestureDelegate OnTapGesture;
        //called for flicks; only the direction and the number of fingers is known
        public static event FlickGestureDelegate OnFlickGesture;
        //called when the user pinches or spreads two fingers on the touchpad; false is passed in for a pinch, true for a spread
        public static event ZoomGestureDelegate OnZoomGesture;
        //called when the user places one finger on the touchpad and then rotates another around it, true will be passed in if it was clockwise
        public static event RotateGestureDelegate OnRotateGesture;
        //called when the user moves a finger on the touchpad
        public static event XYGestureDelegate OnMoveGesture;

        //public events and delegates for SB events
        public delegate void SBEventDelegate();
        public static event SBEventDelegate OnActivated;
        public static event SBEventDelegate OnDeactivated;
        public static event SBEventDelegate OnClose;
        public static event SBEventDelegate OnExit;

        //public keyboard delegates and events
        public delegate void KeyboardKeyDelegate(Keys key, Keys modifier);
        public static event KeyboardKeyDelegate OnKeyDown;
        public static event KeyboardKeyDelegate OnKeyUp;
        public static event KeyboardKeyDelegate OnKeyTyped;


        #endregion

        #region Image Handling
        //*** Buffers for each button and for the touchpad ***//
        //index 0 is the touchpad, the others correspond to the DKs in RZSBSDK_DKTYPE
        private static IntPtr[] bufferParamsPtrs = new IntPtr[NUMBER_OF_DISPLAYS];
        private static IntPtr[] imageDataPtrs = new IntPtr[NUMBER_OF_DISPLAYS];
        //mutexes to protect the image buffers
        private static Mutex[] bufferMuxes = new Mutex[NUMBER_OF_DISPLAYS];

        private static void WriteBitmapDataToImageDataPtr(Bitmap bmp, int index) {
            //Get a copy of the byte array you need to send to your tft
            Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            BitmapData bmpData = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format16bppRgb565);
            //Marshal.Copy(bmpData.Scan0, imgArray, 0, imgArray.Length);
            Utils.memcpy(imageDataPtrs[index], bmpData.Scan0, (UIntPtr)(bmp.Width * bmp.Height * 2));
            bmp.UnlockBits(bmpData);
        }

        public static void SendBufferToSB(SBDisplays display) {
            chkStrt();
            //a thread can wait on a mutex it is already waiting on, so long as it releases it the same number of times as it waits on it
            //the extra layer of mutex waits is in case this function is called from somewhere other than WriteBitmapImagesToSB
            bufferMuxes[(int)display].WaitOne();
            SBMux.WaitOne();
            RZSBSDKCheck(RZSBSDKAPI.RzSBRenderBuffer(convertToRZSBSDKDisplay(display), bufferParamsPtrs[(int)display]));
            SBMux.ReleaseMutex();
            bufferMuxes[(int)display].ReleaseMutex();
        }

        public static void WriteBitmapImageToSB(SBDisplays display, Bitmap bmp) {
            chkStrt();
            bufferMuxes[(int)display].WaitOne();
            WriteBitmapDataToImageDataPtr(bmp, (int)display);
            SendBufferToSB(display);
            bufferMuxes[(int)display].ReleaseMutex();
        }

        public static void SendImageToTouchpad(string filename) {
            chkStrt();
            SBMux.WaitOne();
            RZSBSDKCheck(RZSBSDKAPI.RzSBSetImageTouchpad(filename));
            SBMux.ReleaseMutex();
        }

        //key must be between 1 and 10 (the actual DK number)
        public static void SendImageToDK(int key, bool pressed, string filename) {
            chkStrt();
            SBMux.WaitOne();
            RZSBSDKCheck(RZSBSDKAPI.RzSBSetImageDynamicKey(
                (RZSBSDK_DKTYPE)key,
                pressed ? RZSBSDK_KEYSTATETYPE.RZSBSDK_KEYSTATE_DOWN : RZSBSDK_KEYSTATETYPE.RZSBSDK_KEYSTATE_UP,
                filename));
            SBMux.ReleaseMutex();
        }
        private static RZSBSDK_DISPLAY convertToRZSBSDKDisplay(SBDisplays dsp) {
            return (RZSBSDK_DISPLAY)((1 << 16) | ((int)dsp));
        }
        #endregion

        #region startup and shutdown functions

        /// <summary>
        /// starts the SB
        /// </summary>
        public static void Start() {
            SBMux.WaitOne();
            if (Started) return;
            setupDisplayBuffers();
            RegisterShutdownCallbacks();
            RZSBSDKCheck(RZSBSDKAPI.RzSBStart());
            Started = true;
            SBMux.ReleaseMutex();
        }

        /// <summary>
        /// stops the SB
        /// </summary>
        public static void Stop() {
            SBMux.WaitOne();
            if (!Started) {
                SBMux.ReleaseMutex();
                return;
            }
            forceStop();

            SBMux.ReleaseMutex();
        }

        private static void forceStop() {
            //this RZSBSDKAPI call doesn't need RZSBSDKCheck because it doesn't return anything
            RZSBSDKAPI.RzSBStop(); //stop the SB
            cleanupDisplayBuffers();
            Started = false;
            DeregisterShutdownCallbacks();
        }

        #region functions for registering and deregistering callbacks

        private static void RegisterSBCallbacks() {
            Utils.print("Registering RzSB callbacks...");
            appEventCallbackDelegate += appEventCallback;
            dynamicKeyCallbackDelegate += dynamicKeyCallback;
            gestureCallbackDelegate += gestureCallback;
            keyboardCallbackDelegate += keyboardCallback;
            
            RZSBSDKCheck(RZSBSDKAPI.RzSBAppEventSetCallback(appEventCallbackDelegate));
            RZSBSDKCheck(RZSBSDKAPI.RzSBDynamicKeySetCallback(dynamicKeyCallbackDelegate));
            RZSBSDKCheck(RZSBSDKAPI.RzSBGestureSetCallback(gestureCallbackDelegate));
            RZSBSDKCheck(RZSBSDKAPI.RzSBKeyboardCaptureSetCallback(keyboardCallbackDelegate));

            Utils.println("Done!\n", ConsoleColor.DarkGreen);
        }


        private static void RegisterShutdownCallbacks() {
            Utils.print("Registering shutdown callbacks...");
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
            Utils.println("Done!\n", ConsoleColor.DarkGreen);
        }

        private static void DeregisterShutdownCallbacks() {
            AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
            AppDomain.CurrentDomain.ProcessExit -= CurrentDomain_ProcessExit;
        }

        #region ShutdownCallbacks
        static void CurrentDomain_ProcessExit(object sender, EventArgs e) {
            Utils.println("Shutting down RzSB due to process exit");
            Stop();
        }

        static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e) {
            Utils.println("Shutting down RzSB due to unhandled exception!", ConsoleColor.Red);
            forceStop();
        }
        #endregion

        #endregion

        #region Display Buffer Setup
        private static void setupDisplayBuffers() {
            //setup touchpad display buffers
            imageDataPtrs[0] = Marshal.AllocHGlobal(TP_IMAGEDATA_SIZE);
            RZSBSDK_BUFFERPARAMS tpBufferParams;
            tpBufferParams.PixelType = PIXEL_TYPE.RGB565;
            tpBufferParams.DataSize = TP_IMAGEDATA_SIZE;
            tpBufferParams.PtrData = imageDataPtrs[0];
            bufferParamsPtrs[0] = Marshal.AllocHGlobal(Marshal.SizeOf(tpBufferParams));
            Marshal.StructureToPtr(tpBufferParams, bufferParamsPtrs[0], true);
            bufferMuxes[0] = new Mutex();

            //setup DK image buffers
            for (int i = (int)RZSBSDK_DKTYPE.RZSBSDK_DK_1; i < (int)RZSBSDK_DKTYPE.RZSBSDK_DK_INVALID; i++) {
                imageDataPtrs[i] = Marshal.AllocHGlobal(DK_IMAGEDATA_SIZE);
                RZSBSDK_BUFFERPARAMS dkBufferParams;
                dkBufferParams.PixelType = PIXEL_TYPE.RGB565;
                dkBufferParams.DataSize = DK_IMAGEDATA_SIZE;
                dkBufferParams.PtrData = imageDataPtrs[i];
                bufferParamsPtrs[0] = Marshal.AllocHGlobal(Marshal.SizeOf(dkBufferParams));
                Marshal.StructureToPtr(dkBufferParams, bufferParamsPtrs[i], true);
                bufferMuxes[i] = new Mutex();
            }
        }
        /// <summary>
        /// frees the memory allocated in setupDisplayBuffers()
        /// </summary>
        private static void cleanupDisplayBuffers() {
            Mutex.WaitAll(bufferMuxes);
            for (int i = 0; i < NUMBER_OF_DISPLAYS; i++) {
                Marshal.FreeHGlobal(imageDataPtrs[i]);
                Marshal.FreeHGlobal(bufferParamsPtrs[i]);
                bufferMuxes[i].Dispose();
            }
        }
        #endregion //Display Buffer Setup

        #endregion //start and stop functions

        #region Exception Classes
        [Serializable]
        public class RZSBSDKNotStartedException : Exception
        {
          public RZSBSDKNotStartedException() { }
          public RZSBSDKNotStartedException( string message ) : base( message ) { }
          public RZSBSDKNotStartedException( string message, Exception inner ) : base( message, inner ) { }
          protected RZSBSDKNotStartedException( 
	        System.Runtime.Serialization.SerializationInfo info, 
	        System.Runtime.Serialization.StreamingContext context ) : base( info, context ) { }
        }

        [Serializable]
        public class RZSBSDKException : Exception {

            public string GetSBSDKResultName() {
                return Enum.GetName(typeof(RZSBSDK_HRESULT), result);
            }

            public RZSBSDK_HRESULT result {
                get;
                private set;
            }
            public RZSBSDKException(RZSBSDK_HRESULT res) {
                result = res;
            }
            public RZSBSDKException(RZSBSDK_HRESULT res, string message) : base(message) {
                result = res;
            }
            public RZSBSDKException(RZSBSDK_HRESULT res, string message, Exception inner) : base(message, inner) {
                result = res;
            }
            protected RZSBSDKException(
              RZSBSDK_HRESULT res,
              System.Runtime.Serialization.SerializationInfo info,
              System.Runtime.Serialization.StreamingContext context)
                : base(info, context) { 
                result = res; 
            }
        }

        #endregion

        #region Exception Checking Util Functions
        /// <summary>
        /// pass in a RZSBSDKAPI call and an exception will be thrown if a bad result is given
        /// </summary>
        /// <param name="res">the return value of the RZSBSDKAPI function</param>
        /// <param name="throwmsg">an optional message to include in the event of a bad result</param>
        private static void RZSBSDKCheck(RZSBSDK_HRESULT res, string throwmsg = "") {
            if (res != RZSBSDK_HRESULT.RZSB_OK) {
                throw new RZSBSDKException(res, throwmsg);
            }
        }

        /// <summary>
        /// Throws an RZSBSDKNotStartedException if the RZSB has not been started
        /// </summary>
        private static void chkStrt() {
            if (!Started) throw new RZSBSDKNotStartedException("RZSB not started!");
        }

        #endregion

        #region SB Event Handlers
        private static RZSBSDK_HRESULT gestureCallback(RZSBSDK_GESTURETYPE gesture, uint dwParameters, ushort wXPos, ushort wYPos, ushort wZPos) {
            //Utils.println(String.Format("Gesture recieved: {0}; data: {1}, x: {2}, y: {3}, z: {4}",
                //Enum.GetName(typeof(RZSBSDK_GESTURETYPE), gesture), dwParameters, wXPos, wYPos, wZPos),
                //ConsoleColor.Blue);
            switch (gesture) {
                case RZSBSDK_GESTURETYPE.RZSBSDK_GESTURE_FLICK: if (OnFlickGesture != null) OnFlickGesture(dwParameters, (FlickDirection)wZPos); break;
                case RZSBSDK_GESTURETYPE.RZSBSDK_GESTURE_MOVE: if (OnMoveGesture != null) OnMoveGesture(wXPos, wYPos); break;
                case RZSBSDK_GESTURETYPE.RZSBSDK_GESTURE_PRESS: if (OnPressGesture != null) OnPressGesture(dwParameters, wXPos, wYPos); break;
                case RZSBSDK_GESTURETYPE.RZSBSDK_GESTURE_RELEASE: if (OnReleaseGesture != null) OnReleaseGesture(dwParameters, wXPos, wYPos); break;
                case RZSBSDK_GESTURETYPE.RZSBSDK_GESTURE_ROTATE: if (OnRotateGesture != null) OnRotateGesture(dwParameters == 1); break;
                case RZSBSDK_GESTURETYPE.RZSBSDK_GESTURE_TAP: if (OnTapGesture != null) OnTapGesture(wXPos, wYPos); break;
                case RZSBSDK_GESTURETYPE.RZSBSDK_GESTURE_ZOOM: if (OnZoomGesture != null) OnZoomGesture(dwParameters == 1); break;
                default: Utils.printf("An unknown gesture received: {0}", Enum.GetName(typeof(RZSBSDK_GESTURETYPE), gesture)); break;
            }

            return RZSBSDK_HRESULT.RZSB_OK;
        }

        private static RZSBSDK_HRESULT dynamicKeyCallback(RZSBSDK_DKTYPE dynamicKey, RZSBSDK_KEYSTATETYPE dynamicKeyState) {
            //Utils.printf("RzSB dynamic key event recieved: key = {0}; state = {1}", 
            //Enum.GetName(typeof(RZSBSDK_DKTYPE), dynamicKey), 
            //Enum.GetName(typeof(RZSBSDK_KEYSTATETYPE), dynamicKeyState));
            if (OnDynamicKeyEvent != null) OnDynamicKeyEvent((int)dynamicKey, dynamicKeyState==RZSBSDK_KEYSTATETYPE.RZSBSDK_KEYSTATE_DOWN);

            return RZSBSDK_HRESULT.RZSB_OK;
        }

        private static RZSBSDK_HRESULT appEventCallback(RZSBSDK_EVENTTYPETYPE rzEventType, uint dwParam1, uint dwParam2) {
            Utils.printf("RzSB App event recieved: rzEventType = {0}; dwParam1 = {1}; dwParam2 = {2}", 
                Enum.GetName(typeof(RZSBSDK_EVENTTYPETYPE), rzEventType), 
                dwParam1, 
                dwParam2);
            if (OnAppEvent != null) OnAppEvent((SBEvent)rzEventType);
            if (rzEventType == RZSBSDK_EVENTTYPETYPE.RZSBSDK_EVENT_CLOSE) {
                Environment.Exit(0);//the SB will automatically catch this and shutdown
            }

            return RZSBSDK_HRESULT.RZSB_OK;
        }
        //callback for keyboard events when the keyboard is captured
        private static RZSBSDK_HRESULT keyboardCallback(uint uMsg, UIntPtr wParam, IntPtr lParam) {
            switch (uMsg) {
                case WM_KEYUP: if (OnKeyUp != null) OnKeyUp((Keys)wParam, (Keys)lParam); break;
                case WM_KEYDOWN: if (OnKeyDown != null) OnKeyDown((Keys)wParam, (Keys)lParam); break;
                case WM_CHAR: if (OnKeyTyped != null) OnKeyTyped((Keys)wParam, (Keys)lParam); break;
                default: Utils.println("Invalid Keyboard message (" + uMsg + ")", ConsoleColor.Red); break;
            }
            return RZSBSDK_HRESULT.RZSB_OK;
        }


        #endregion

    }
}
