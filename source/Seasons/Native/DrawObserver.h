#pragma once
#include <cstdint>

namespace season::observe {
// Diagnostic ABI only. Numeric COM identities in a receipt do not own references.
constexpr uint32_t Magic=0x53444f31, Version=2, Capacity=64;
struct Binding {
    uint64_t object, resource;
    uint32_t dimension, format, width, height, mipLevels, arraySize;
};
struct Constant {
    uint64_t buffer, cpuSource;
    uint32_t byteWidth, copiedBytes;
    uint8_t bytes[256];
};
struct Record {
    uint64_t sequence, tick, driver, context, device, nativeDraw, psOwner, vsOwner, ps, vs;
    uint32_t thread, contextType, creationFlags, start, count, counterBefore, counterAfter, complete;
    uint32_t instances, drawKind;
    uint64_t captureEndTick;
    Binding views[12], targets[4], depth;
    Constant psConstants[14], vsConstants[14];
    float viewport[6];
};
struct Request {
    uint32_t magic, version, size, durationMs;
    uint64_t moduleBase, context, device;
    // Written only by the native worker, except cancel set by the controller.
    int32_t cancel, status, error, records, active, skipped, rejected, restoreAttempts;
    uint32_t codeRestored, protectionRestored, callbacksDrained, referencesReleased;
    uint32_t originalProtection, finalProtection;
    uint64_t target, trampoline, workerThread;
    uint32_t failedThread, failedStage, failedThreadExit, failedThreadExitRead;
    Record rows[Capacity];
};
static_assert(sizeof(Binding)==40 && sizeof(Constant)==280 && sizeof(Record)==8672);
static_assert(sizeof(Request)==555144);
}
