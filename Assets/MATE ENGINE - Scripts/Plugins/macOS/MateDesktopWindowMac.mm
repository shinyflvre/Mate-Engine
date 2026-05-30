#import <Cocoa/Cocoa.h>
#import <CoreGraphics/CoreGraphics.h>
#include <math.h>
#include <string.h>
#include <stdlib.h>

typedef struct MateDWPoint
{
    double x;
    double y;
} MateDWPoint;

typedef struct MateDWRect
{
    double left;
    double top;
    double right;
    double bottom;
} MateDWRect;

typedef struct __attribute__((packed)) MateDWWindowInfo
{
    uint32_t windowId;
    uint32_t ownerPid;
    int32_t layer;
    float alpha;
    int32_t left;
    int32_t top;
    int32_t right;
    int32_t bottom;
    bool onScreen;
    char ownerName[256];
    char title[256];
} MateDWWindowInfo;

static void CopyUTF8(NSString *source, char *destination, size_t capacity)
{
    if (capacity == 0) return;
    destination[0] = '\0';
    if (source == nil) return;
    const char *text = [source UTF8String];
    if (text == NULL) return;
    strncpy(destination, text, capacity - 1);
    destination[capacity - 1] = '\0';
}

static bool RectFromDictionary(CFDictionaryRef dict, MateDWRect *rect)
{
    if (dict == NULL || rect == NULL) return false;
    CGRect cgRect;
    if (!CGRectMakeWithDictionaryRepresentation(dict, &cgRect)) return false;
    rect->left = CGRectGetMinX(cgRect);
    rect->top = CGRectGetMinY(cgRect);
    rect->right = CGRectGetMaxX(cgRect);
    rect->bottom = CGRectGetMaxY(cgRect);
    return true;
}

static bool RectFromWindowDictionary(NSDictionary *window, MateDWRect *rect)
{
    return RectFromDictionary((__bridge CFDictionaryRef)window[(id)kCGWindowBounds], rect);
}

static NSArray *CopyWindowInfoArray(void)
{
    CFArrayRef windows = CGWindowListCopyWindowInfo(kCGWindowListOptionOnScreenOnly, kCGNullWindowID);
    return CFBridgingRelease(windows);
}

static NSArray *gWindowSnapshot = nil;
static CFTimeInterval gWindowSnapshotTime = 0.0;
static const CFTimeInterval kWindowSnapshotMaxAgeSeconds = 1.0 / 30.0;
static NSInteger gOwnWindowNumber = 0;
static bool gOwnTopmostStateKnown = false;
static bool gOwnTopmostState = false;
static NSInteger gOwnTopmostWindowNumber = 0;

static NSArray *CopyAndCacheWindowInfoArray(void)
{
    NSArray *windows = CopyWindowInfoArray();
    gWindowSnapshot = windows;
    gWindowSnapshotTime = windows != nil ? CFAbsoluteTimeGetCurrent() : 0.0;
    return windows;
}

static bool IsWindowSnapshotFresh(CFTimeInterval maxAgeSeconds)
{
    if (gWindowSnapshot == nil || maxAgeSeconds < 0.0) return false;
    return CFAbsoluteTimeGetCurrent() - gWindowSnapshotTime <= maxAgeSeconds;
}

static NSArray *CachedWindowInfoArray(bool allowRefresh, CFTimeInterval maxAgeSeconds)
{
    if (gWindowSnapshot != nil && IsWindowSnapshotFresh(maxAgeSeconds)) return gWindowSnapshot;
    if (!allowRefresh) return gWindowSnapshot;
    return CopyAndCacheWindowInfoArray();
}

static CGRect CGBoundsForScreen(NSScreen *screen)
{
    if (screen == nil) return CGRectZero;
    NSNumber *screenNumber = screen.deviceDescription[@"NSScreenNumber"];
    if (screenNumber == nil) return CGRectZero;
    return CGDisplayBounds((CGDirectDisplayID)[screenNumber unsignedIntValue]);
}

static NSScreen *ScreenForCGPoint(double x, double y, CGRect *cgBounds)
{
    for (NSScreen *screen in [NSScreen screens])
    {
        CGRect bounds = CGBoundsForScreen(screen);
        if (CGRectIsEmpty(bounds)) continue;
        if (x >= CGRectGetMinX(bounds) && x < CGRectGetMaxX(bounds) &&
            y >= CGRectGetMinY(bounds) && y < CGRectGetMaxY(bounds))
        {
            if (cgBounds != NULL) *cgBounds = bounds;
            return screen;
        }
    }

    NSScreen *fallback = [NSScreen mainScreen];
    if (cgBounds != NULL) *cgBounds = CGBoundsForScreen(fallback);
    return fallback;
}

static bool IsUsableOwnWindow(NSWindow *window)
{
    if (window == nil || ![window isVisible] || [window isMiniaturized]) return false;
    NSRect frame = [window frame];
    return NSWidth(frame) > 1.0 && NSHeight(frame) > 1.0;
}

static void CacheOwnWindow(NSWindow *window)
{
    if (window == nil) return;
    NSInteger windowNumber = [window windowNumber];
    if (windowNumber > 0) gOwnWindowNumber = windowNumber;
}

static NSWindow *FindOwnWindowByNumber(NSInteger windowNumber)
{
    if (windowNumber <= 0) return nil;
    for (NSWindow *window in [NSApp windows])
    {
        if ([window windowNumber] == windowNumber && IsUsableOwnWindow(window)) return window;
    }
    return nil;
}

static NSWindow *LargestVisibleOwnWindow(void)
{
    NSWindow *bestWindow = nil;
    double bestArea = -1.0;
    for (NSWindow *window in [NSApp windows])
    {
        if (!IsUsableOwnWindow(window)) continue;
        NSRect frame = [window frame];
        double area = NSWidth(frame) * NSHeight(frame);
        if (area > bestArea)
        {
            bestArea = area;
            bestWindow = window;
        }
    }
    return bestWindow;
}

static NSWindow *CurrentOwnWindow(void)
{
    NSWindow *window = [NSApp mainWindow] ?: [NSApp keyWindow];
    if (IsUsableOwnWindow(window))
    {
        CacheOwnWindow(window);
        return window;
    }

    window = FindOwnWindowByNumber(gOwnWindowNumber);
    if (window != nil) return window;

    window = LargestVisibleOwnWindow();
    if (window != nil) CacheOwnWindow(window);
    return window;
}

static bool RectFromCocoaScreenRect(NSRect screenRect, NSScreen *screen, MateDWRect *rect)
{
    if (rect == NULL || screen == nil) return false;

    NSRect screenFrame = [screen frame];
    CGRect cgBounds = CGBoundsForScreen(screen);
    if (CGRectIsEmpty(cgBounds)) return false;

    double localLeft = NSMinX(screenRect) - NSMinX(screenFrame);
    double localBottom = NSMinY(screenRect) - NSMinY(screenFrame);
    double localTop = localBottom + NSHeight(screenRect);
    rect->left = CGRectGetMinX(cgBounds) + localLeft;
    rect->top = CGRectGetMinY(cgBounds) + CGRectGetHeight(cgBounds) - localTop;
    rect->right = rect->left + NSWidth(screenRect);
    rect->bottom = rect->top + NSHeight(screenRect);
    return true;
}

static bool RectFromAppKitWindow(NSWindow *window, bool contentOnly, MateDWRect *rect)
{
    if (window == nil) return false;
    NSScreen *screen = [window screen] ?: [NSScreen mainScreen];
    if (screen == nil) return false;

    NSRect frame = [window frame];
    NSRect screenRect = contentOnly ? [window contentRectForFrameRect:frame] : frame;
    return RectFromCocoaScreenRect(screenRect, screen, rect);
}

static NSDictionary *FindWindowInArray(NSArray *windows, uint32_t windowId, int *index)
{
    if (windowId == 0 || windows == nil) return nil;
    int i = 0;
    for (NSDictionary *window in windows)
    {
        NSNumber *number = window[(id)kCGWindowNumber];
        if (number != nil && [number unsignedIntValue] == windowId)
        {
            if (index != NULL) *index = i;
            return window;
        }
        i++;
    }
    return nil;
}

static NSDictionary *FindWindowInSnapshot(uint32_t windowId, bool refreshOnMiss, int *index)
{
    bool hadFreshSnapshot = IsWindowSnapshotFresh(kWindowSnapshotMaxAgeSeconds);
    NSArray *windows = CachedWindowInfoArray(refreshOnMiss, kWindowSnapshotMaxAgeSeconds);
    NSDictionary *window = FindWindowInArray(windows, windowId, index);
    if (window != nil || !refreshOnMiss || !hadFreshSnapshot) return window;
    windows = CopyAndCacheWindowInfoArray();
    return FindWindowInArray(windows, windowId, index);
}

static bool FillInfo(NSDictionary *window, MateDWWindowInfo *outInfo, int z)
{
    if (window == nil || outInfo == NULL) return false;
    memset(outInfo, 0, sizeof(MateDWWindowInfo));

    NSNumber *windowId = window[(id)kCGWindowNumber];
    NSNumber *ownerPid = window[(id)kCGWindowOwnerPID];
    NSNumber *layer = window[(id)kCGWindowLayer];
    NSNumber *alpha = window[(id)kCGWindowAlpha];
    NSNumber *onScreen = window[(id)kCGWindowIsOnscreen];

    MateDWRect rect;
    if (!RectFromWindowDictionary(window, &rect)) return false;

    outInfo->windowId = windowId != nil ? [windowId unsignedIntValue] : 0;
    outInfo->ownerPid = ownerPid != nil ? [ownerPid unsignedIntValue] : 0;
    outInfo->layer = layer != nil ? [layer intValue] : 0;
    outInfo->alpha = alpha != nil ? [alpha floatValue] : 1.0f;
    outInfo->left = (int32_t)lround(rect.left);
    outInfo->top = (int32_t)lround(rect.top);
    outInfo->right = (int32_t)lround(rect.right);
    outInfo->bottom = (int32_t)lround(rect.bottom);
    outInfo->onScreen = onScreen == nil ? true : [onScreen boolValue];
    CopyUTF8(window[(id)kCGWindowOwnerName], outInfo->ownerName, sizeof(outInfo->ownerName));
    CopyUTF8(window[(id)kCGWindowName], outInfo->title, sizeof(outInfo->title));
    (void)z;
    return outInfo->windowId != 0;
}

extern "C" int MateDWCopyWindowInfos(MateDWWindowInfo *buffer, int capacity)
{
    if (buffer == NULL || capacity <= 0) return 0;
    @autoreleasepool
    {
        NSArray *windows = CopyAndCacheWindowInfoArray();
        if (windows == nil) return 0;

        int copied = 0;
        for (NSDictionary *window in windows)
        {
            if (copied >= capacity) break;
            if (FillInfo(window, &buffer[copied], copied)) copied++;
        }
        return copied;
    }
}

extern "C" bool MateDWGetWindowRect(uint32_t windowId, MateDWRect *rect)
{
    if (windowId == 0 || rect == NULL) return false;
    @autoreleasepool
    {
        NSDictionary *window = FindWindowInSnapshot(windowId, true, NULL);
        return RectFromWindowDictionary(window, rect);
    }
}

extern "C" bool MateDWIsWindowAlive(uint32_t windowId)
{
    if (windowId == 0) return false;
    @autoreleasepool
    {
        return FindWindowInSnapshot(windowId, true, NULL) != nil;
    }
}

extern "C" bool MateDWIsWindowAbove(uint32_t aWindowId, uint32_t bWindowId)
{
    if (aWindowId == 0 || bWindowId == 0 || aWindowId == bWindowId) return false;
    @autoreleasepool
    {
        int ai = -1;
        int bi = -1;
        NSArray *windows = CachedWindowInfoArray(true, kWindowSnapshotMaxAgeSeconds);
        FindWindowInArray(windows, aWindowId, &ai);
        FindWindowInArray(windows, bWindowId, &bi);
        if (ai >= 0 && bi >= 0) return ai < bi;

        windows = CopyAndCacheWindowInfoArray();
        ai = -1;
        bi = -1;
        FindWindowInArray(windows, aWindowId, &ai);
        FindWindowInArray(windows, bWindowId, &bi);
        return ai >= 0 && bi >= 0 && ai < bi;
    }
}

static bool FindOwnWindowRect(MateDWRect *rect)
{
    pid_t pid = [[NSProcessInfo processInfo] processIdentifier];
    NSArray *windows = CopyWindowInfoArray();
    double bestArea = -1.0;
    bool found = false;

    for (NSDictionary *window in windows)
    {
        NSNumber *ownerPid = window[(id)kCGWindowOwnerPID];
        NSNumber *layer = window[(id)kCGWindowLayer];
        if (ownerPid == nil || [ownerPid intValue] != pid) continue;
        if (layer != nil && [layer intValue] != 0) continue;

        MateDWRect candidate;
        if (!RectFromWindowDictionary(window, &candidate)) continue;
        double area = (candidate.right - candidate.left) * (candidate.bottom - candidate.top);
        if (area > bestArea)
        {
            bestArea = area;
            *rect = candidate;
            found = true;
        }
    }
    return found;
}

extern "C" bool MateDWGetOwnWindowRect(MateDWRect *rect)
{
    if (rect == NULL) return false;
    @autoreleasepool
    {
        if (RectFromAppKitWindow(CurrentOwnWindow(), false, rect)) return true;
        return FindOwnWindowRect(rect);
    }
}

extern "C" bool MateDWGetOwnClientRect(MateDWRect *rect)
{
    if (rect == NULL) return false;
    @autoreleasepool
    {
        NSWindow *window = CurrentOwnWindow();
        if (window == nil) return FindOwnWindowRect(rect);
        if (RectFromAppKitWindow(window, true, rect)) return true;
        return FindOwnWindowRect(rect);
    }
}

extern "C" bool MateDWMoveOwnWindow(int x, int y, int width, int height)
{
    if (width <= 0 || height <= 0) return false;
    @autoreleasepool
    {
        NSWindow *window = CurrentOwnWindow();
        if (window == nil) return false;

        CGRect cgBounds;
        NSScreen *screen = ScreenForCGPoint(x, y, &cgBounds);
        if (screen == nil || CGRectIsEmpty(cgBounds)) screen = [window screen] ?: [NSScreen mainScreen];
        if (screen == nil) return false;

        NSRect screenFrame = [screen frame];
        if (CGRectIsEmpty(cgBounds)) cgBounds = CGBoundsForScreen(screen);
        if (CGRectIsEmpty(cgBounds)) return false;

        CGFloat cocoaX = NSMinX(screenFrame) + (x - CGRectGetMinX(cgBounds));
        CGFloat cocoaY = NSMinY(screenFrame) + CGRectGetHeight(cgBounds) - (y - CGRectGetMinY(cgBounds)) - height;
        [window setFrame:NSMakeRect(cocoaX, cocoaY, width, height) display:YES animate:NO];
        return true;
    }
}

extern "C" void MateDWSetOwnTopmost(bool enabled)
{
    @autoreleasepool
    {
        NSWindow *window = CurrentOwnWindow();
        if (window == nil) return;
        NSInteger windowNumber = [window windowNumber];
        if (gOwnTopmostStateKnown &&
            gOwnTopmostWindowNumber == windowNumber &&
            gOwnTopmostState == enabled)
        {
            return;
        }
        [window setLevel:(enabled ? NSPopUpMenuWindowLevel : NSNormalWindowLevel)];
        gOwnTopmostStateKnown = true;
        gOwnTopmostState = enabled;
        gOwnTopmostWindowNumber = windowNumber;
    }
}

extern "C" bool MateDWGetCursorPosition(MateDWPoint *point)
{
    if (point == NULL) return false;
    CGEventRef event = CGEventCreate(NULL);
    if (event == NULL) return false;
    CGPoint location = CGEventGetLocation(event);
    CFRelease(event);
    point->x = location.x;
    point->y = location.y;
    return true;
}

extern "C" int MateDWGetMonitorCount(void)
{
    uint32_t count = 0;
    CGGetActiveDisplayList(0, NULL, &count);
    return (int)count;
}

extern "C" bool MateDWGetMonitorRect(int index, MateDWRect *rect)
{
    if (rect == NULL || index < 0) return false;
    uint32_t count = 0;
    CGGetActiveDisplayList(0, NULL, &count);
    if ((uint32_t)index >= count) return false;

    CGDirectDisplayID *displays = (CGDirectDisplayID *)calloc(count, sizeof(CGDirectDisplayID));
    if (displays == NULL) return false;

    CGGetActiveDisplayList(count, displays, &count);
    CGRect bounds = CGDisplayBounds(displays[index]);
    free(displays);
    rect->left = CGRectGetMinX(bounds);
    rect->top = CGRectGetMinY(bounds);
    rect->right = CGRectGetMaxX(bounds);
    rect->bottom = CGRectGetMaxY(bounds);
    return true;
}
