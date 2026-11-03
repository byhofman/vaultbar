import ctypes, sys

x = ctypes.CDLL("libX11.so.6")
x.XOpenDisplay.restype = ctypes.c_void_p
V, UL, I = ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int
x.XDefaultRootWindow.restype = UL
x.XDefaultRootWindow.argtypes = [V]
x.XCreateSimpleWindow.restype = UL
x.XCreateSimpleWindow.argtypes = [V, UL, I, I, ctypes.c_uint, ctypes.c_uint, ctypes.c_uint, UL, UL]
x.XInternAtom.restype = UL
x.XInternAtom.argtypes = [V, ctypes.c_char_p, I]
x.XSelectInput.argtypes = [V, UL, ctypes.c_long]
x.XMapWindow.argtypes = [V, UL]
x.XStoreName.argtypes = [V, UL, ctypes.c_char_p]
x.XSetInputFocus.argtypes = [V, UL, I, UL]
x.XNextEvent.argtypes = [V, ctypes.c_void_p]
x.XChangeProperty.argtypes = [V, UL, UL, UL, I, I, ctypes.c_void_p, I]
x.XLookupString.argtypes = [ctypes.c_void_p, ctypes.c_char_p, I, ctypes.POINTER(UL), ctypes.c_void_p]
x.XConfigureWindow.argtypes = [V, UL, ctypes.c_uint, ctypes.c_void_p]
x.XFlush.argtypes = [V]


class XWindowChanges(ctypes.Structure):
    _fields_ = [("x", I), ("y", I), ("width", I), ("height", I), ("border_width", I), ("sibling", UL), ("stack_mode", I)]


d = x.XOpenDisplay(None)
root = x.XDefaultRootWindow(d)
active_atom = x.XInternAtom(d, b"_NET_ACTIVE_WINDOW", 0)
XA_WINDOW = 33
out = open(sys.argv[1], "w", buffering=1, encoding="utf-8")
_w = out.write
out.write = lambda s: (_w(s), out.flush())


def activate(w):
    x.XSetInputFocus(d, w, 2, 0)
    val = UL(w)
    x.XChangeProperty(d, root, active_atom, XA_WINDOW, 32, 0, ctypes.byref(val), 1)
    x.XFlush(d)


x.XSelectInput(d, root, (1 << 20) | (1 << 19))
target = x.XCreateSimpleWindow(d, root, 10, 10, 400, 120, 0, 0, 0xFFFFFF)
x.XStoreName(d, target, b"Login - Example App")
x.XSelectInput(d, target, 1)
x.XMapWindow(d, target)
activate(target)
out.write("READY\n")

ev = ctypes.create_string_buffer(192)
buf = ctypes.create_string_buffer(32)
while True:
    x.XNextEvent(d, ev)
    t = ctypes.c_int.from_buffer(ev, 0).value
    if t == 20:
        w = UL.from_buffer(ev, 40).value
        x.XMapWindow(d, w)
        activate(w)
    elif t == 23:
        w = UL.from_buffer(ev, 40).value
        ch = XWindowChanges(ctypes.c_int.from_buffer(ev, 48).value, ctypes.c_int.from_buffer(ev, 52).value,
                            ctypes.c_int.from_buffer(ev, 56).value, ctypes.c_int.from_buffer(ev, 60).value, 0, 0, 0)
        x.XConfigureWindow(d, w, UL.from_buffer(ev, 88).value & 0xF, ctypes.byref(ch))
    elif t == 33 and UL.from_buffer(ev, 40).value == active_atom:
        activate(UL.from_buffer(ev, 32).value)
    elif t == 34:
        x.XRefreshKeyboardMapping(ev)
    elif t == 2 and UL.from_buffer(ev, 32).value == target:
        ks = UL()
        n = x.XLookupString(ev, buf, 32, ctypes.byref(ks), None)
        if ks.value == 0xff09: out.write("<TAB>")
        elif ks.value == 0xff0d: out.write("<ENTER>\n")
        elif 0x20 <= ks.value <= 0xff: out.write(chr(ks.value))
        elif ks.value & 0xff000000 == 0x01000000: out.write(chr(ks.value & 0xffffff))
