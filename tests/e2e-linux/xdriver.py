import ctypes, ctypes.util, struct, sys, time, zlib

x = ctypes.CDLL("libX11.so.6")
xt = ctypes.CDLL("libXtst.so.6")
x.XOpenDisplay.restype = ctypes.c_void_p
x.XDefaultRootWindow.restype = ctypes.c_ulong
x.XDefaultRootWindow.argtypes = [ctypes.c_void_p]
x.XInternAtom.restype = ctypes.c_ulong
x.XInternAtom.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int]
x.XGetWindowProperty.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_long, ctypes.c_long, ctypes.c_int, ctypes.c_ulong,
                                 ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(ctypes.c_void_p)]
x.XQueryTree.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(ctypes.POINTER(ctypes.c_ulong)), ctypes.POINTER(ctypes.c_uint)]
x.XFetchName.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(ctypes.c_char_p)]
x.XStringToKeysym.restype = ctypes.c_ulong
x.XStringToKeysym.argtypes = [ctypes.c_char_p]
x.XKeysymToKeycode.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
x.XKeysymToKeycode.restype = ctypes.c_ubyte
x.XGetImage.restype = ctypes.c_void_p
x.XGetImage.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int, ctypes.c_int, ctypes.c_uint, ctypes.c_uint, ctypes.c_ulong, ctypes.c_int]
x.XGetPixel = None
x.XFlush.argtypes = [ctypes.c_void_p]
x.XSync.argtypes = [ctypes.c_void_p, ctypes.c_int]
xt.XTestFakeKeyEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]


class XWindowAttributes(ctypes.Structure):
    _fields_ = [("x", ctypes.c_int), ("y", ctypes.c_int), ("width", ctypes.c_int), ("height", ctypes.c_int), ("border_width", ctypes.c_int),
                ("depth", ctypes.c_int), ("visual", ctypes.c_void_p), ("root", ctypes.c_ulong), ("class_", ctypes.c_int), ("bit_gravity", ctypes.c_int),
                ("win_gravity", ctypes.c_int), ("backing_store", ctypes.c_int), ("backing_planes", ctypes.c_ulong), ("backing_pixel", ctypes.c_ulong),
                ("save_under", ctypes.c_int), ("colormap", ctypes.c_ulong), ("map_installed", ctypes.c_int), ("map_state", ctypes.c_int),
                ("all_event_masks", ctypes.c_long), ("your_event_mask", ctypes.c_long), ("do_not_propagate_mask", ctypes.c_long),
                ("override_redirect", ctypes.c_int), ("screen", ctypes.c_void_p)]


class XImage(ctypes.Structure):
    _fields_ = [("width", ctypes.c_int), ("height", ctypes.c_int), ("xoffset", ctypes.c_int), ("format", ctypes.c_int), ("data", ctypes.c_void_p),
                ("byte_order", ctypes.c_int), ("bitmap_unit", ctypes.c_int), ("bitmap_bit_order", ctypes.c_int), ("bitmap_pad", ctypes.c_int),
                ("depth", ctypes.c_int), ("bytes_per_line", ctypes.c_int), ("bits_per_pixel", ctypes.c_int)]


x.XGetWindowAttributes.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(XWindowAttributes)]
d = x.XOpenDisplay(None)
root = x.XDefaultRootWindow(d)


def walk(w, depth=0, out=None):
    out = [] if out is None else out
    r, p, ch, n = ctypes.c_ulong(), ctypes.c_ulong(), ctypes.POINTER(ctypes.c_ulong)(), ctypes.c_uint()
    x.XQueryTree(d, w, ctypes.byref(r), ctypes.byref(p), ctypes.byref(ch), ctypes.byref(n))
    for i in range(n.value):
        c = ch[i]
        name = ctypes.c_char_p()
        x.XFetchName(d, c, ctypes.byref(name))
        a = XWindowAttributes()
        x.XGetWindowAttributes(d, c, ctypes.byref(a))
        out.append((c, name.value.decode() if name.value else "", a.map_state, a.x, a.y, a.width, a.height, depth))
        walk(c, depth + 1, out)
    return out


def find():
    for w in walk(root):
        if w[1] == "Vaultbar" and w[2] == 2:
            return w
    return None


def shot(path):
    w = find()
    if not w:
        print("not visible"); return
    img = ctypes.cast(x.XGetImage(d, w[0], 0, 0, w[5], w[6], 0xFFFFFFFF, 2), ctypes.POINTER(XImage)).contents
    raw = ctypes.string_at(img.data, img.bytes_per_line * img.height)
    rows = b"".join(b"\x00" + bytes(b for px in range(img.width) for b in (raw[y * img.bytes_per_line + px * 4 + 2], raw[y * img.bytes_per_line + px * 4 + 1], raw[y * img.bytes_per_line + px * 4])) for y in range(img.height))
    def chunk(t, data): return struct.pack(">I", len(data)) + t + data + struct.pack(">I", zlib.crc32(t + data) & 0xffffffff)
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", img.width, img.height, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")
    open(path, "wb").write(png)
    print("saved", img.width, img.height)


SHIFTED = {c: c.lower() for c in "ABCDEFGHIJKLMNOPQRSTUVWXYZ"}
SHIFTED.update({"!": "1", "@": "2", "#": "3", "$": "4", "%": "5", "^": "6", "&": "7", "*": "8", "(": "9", ")": "0", "_": "minus", "+": "equal"})
NAMES = {"-": "minus", " ": "space", ".": "period", "=": "equal"}


def tap(keysym_name, mods=()):
    for m in mods: xt.XTestFakeKeyEvent(d, x.XKeysymToKeycode(d, x.XStringToKeysym(m.encode())), 1, 0)
    kc = x.XKeysymToKeycode(d, x.XStringToKeysym(keysym_name.encode()))
    xt.XTestFakeKeyEvent(d, kc, 1, 0); xt.XTestFakeKeyEvent(d, kc, 0, 0)
    for m in reversed(mods): xt.XTestFakeKeyEvent(d, x.XKeysymToKeycode(d, x.XStringToKeysym(m.encode())), 0, 0)
    x.XSync(d, 0); time.sleep(0.03)


def typ(text):
    for ch in text:
        if ch in SHIFTED: tap(SHIFTED[ch], ["Shift_L"])
        else: tap(NAMES.get(ch, ch))


for cmd in sys.argv[1:]:
    op, _, arg = cmd.partition(":")
    if op == "list":
        for w in walk(root):
            if w[2] == 2 and w[5] > 50: print(w)
    elif op == "shot": shot(arg)
    elif op == "type": typ(arg)
    elif op == "key": tap(arg.split("+")[-1], ["Control_L" if m == "ctrl" else "Shift_L" if m == "shift" else "Alt_L" for m in arg.split("+")[:-1]])
    elif op == "sleep": time.sleep(float(arg))
    elif op == "wait":
        t0 = time.time()
        while not find() and time.time() - t0 < float(arg or 20): time.sleep(0.2)
        print("visible after %.1fs" % (time.time() - t0) if find() else "never visible")
    elif op == "activate":
        tw = next(w for w in walk(root) if w[1] == arg)
        ev = ctypes.create_string_buffer(192)
        struct.pack_into("i", ev, 0, 33); struct.pack_into("Q", ev, 32, tw[0]); struct.pack_into("Q", ev, 40, x.XInternAtom(d, b"_NET_ACTIVE_WINDOW", 0)); struct.pack_into("i", ev, 48, 32); struct.pack_into("q", ev, 56, 2)
        x.XSendEvent.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int, ctypes.c_long, ctypes.c_void_p]
        x.XSendEvent(d, root, 0, (1 << 20) | (1 << 19), ev); x.XSync(d, 0); print("activated", arg)
    elif op == "focus":
        w = find()
        x.XSetInputFocus(d, ctypes.c_ulong(w[0]), 2, 0); x.XSync(d, 0); print("focused", w[0])
