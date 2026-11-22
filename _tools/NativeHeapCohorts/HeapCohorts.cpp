#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <evntrace.h>
#include <evntcons.h>
#include <algorithm>
#include <cstdio>
#include <cstdint>
#include <cctype>
#include <cstring>
#include <fstream>
#include <map>
#include <sstream>
#include <stdexcept>
#include <string>
#include <tuple>
#include <unordered_map>
#include <vector>

using U64 = uint64_t;
static constexpr GUID HeapProvider{0x222962ab,0x6180,0x4b88,{0xa8,0x25,0x34,0x6b,0x75,0xf2,0xa2,0x4a}};
static constexpr GUID StackProvider{0xdef2fe46,0x7bd6,0x4b80,{0xbd,0x94,0xf5,0x7f,0xe2,0x0d,0x0c,0xe3}};
enum Operation : uint8_t { Allocate = 33, Reallocate = 34, Destroy = 35, Release = 36 };

struct StackKey {
    U64 qpc;
    ULONG thread;
    auto Tie() const { return std::tie(qpc, thread); }
    bool operator<(const StackKey& other) const { return Tie() < other.Tie(); }
};
struct HeapEvent {
    StackKey stack;
    U64 heap, address, size, oldAddress;
    Operation operation;
};
struct Phase {
    U64 qpc;
    std::string name;
};
struct Block {
    U64 start, end, size, heap, address;
    StackKey stack, endStack;
    Operation endOperation;
};
struct Module {
    U64 base, size;
    std::string name;
};
struct StackReference {
    unsigned operations{}, stackEvents{};
    size_t index{};
};
struct Totals {
    U64 count{}, bytes{};
    void Add(U64 size) { ++count; bytes += size; }
};
struct Cohort {
    U64 after, through;
    std::string module;
};
struct Trace {
    ULONG pid{};
    U64 heap{}, lastPhase{};
    bool stackPass{}, invalid{};
    U64 malformed{}, missingFree{}, missingRealloc{}, failedAlloc{}, failedRealloc{}, replaced{}, destroyCount{};
    U64 allocCount{}, reallocCount{}, releaseCount{}, moves{}, sameAddress{}, cutoffConflicts{};
    std::vector<HeapEvent> events;
    std::map<StackKey, StackReference> stackKeys;
    std::map<std::vector<U64>, size_t> uniqueStacks;
    std::vector<std::vector<U64>> stacks{{}}; // 0 means no unambiguous correlated stack.
    std::vector<Block> blocks;
};

template<class T> static T Read(const BYTE* bytes, size_t offset)
{
    T value;
    memcpy(&value, bytes + offset, sizeof(value));
    return value;
}

static void ReadHeap(Trace& trace, const EVENT_RECORD& record)
{
    if (record.EventHeader.ProcessId != trace.pid) return;
    const auto qpc = static_cast<U64>(record.EventHeader.TimeStamp.QuadPart);
    if (qpc > trace.lastPhase) return;
    auto op = static_cast<Operation>(record.EventHeader.EventDescriptor.Opcode);
    if (op < Allocate || op > Release) return;
    const unsigned expected[] = {28, 44, 8, 20};
    if (!(record.EventHeader.Flags & EVENT_HEADER_FLAG_64_BIT_HEADER)
        || record.UserDataLength != expected[op - Allocate]) {
        ++trace.malformed;
        return;
    }
    const auto* data = static_cast<const BYTE*>(record.UserData);
    HeapEvent event{{qpc, record.EventHeader.ThreadId},
        Read<U64>(data, 0), 0, 0, 0, op};
    if (trace.heap && event.heap != trace.heap) return;
    switch (op) {
    case Allocate:
        event.address = Read<U64>(data, 16);
        event.size = Read<U64>(data, 8);
        break;
    case Reallocate:
        event.address = Read<U64>(data, 8);
        event.oldAddress = Read<U64>(data, 16);
        event.size = Read<U64>(data, 24);
        break;
    case Release:
        event.address = Read<U64>(data, 8);
        break;
    case Destroy:
        break;
    }
    constexpr size_t maxEvents = 12'000'000;
    if (trace.events.size() >= maxEvents) {
        trace.invalid = true;
        return;
    }
    trace.events.push_back(event);
}

static void ReadStack(Trace& trace, const EVENT_RECORD& record)
{
    if (record.EventHeader.EventDescriptor.Opcode != 32 || record.UserDataLength < 24
        || (record.UserDataLength - 16) % 8 != 0) return;
    const auto* data = static_cast<const BYTE*>(record.UserData);
    if (Read<ULONG>(data, 8) != trace.pid) return;
    StackKey key{Read<U64>(data, 0), Read<ULONG>(data, 12)};
    auto found = trace.stackKeys.find(key);
    if (found == trace.stackKeys.end()) return;
    auto& entry = found->second;
    ++entry.stackEvents;
    if (entry.operations != 1 || entry.stackEvents != 1) {
        entry.index = 0;
        return;
    }
    std::vector<U64> stack;
    for (size_t offset = 16; offset < record.UserDataLength; offset += 8)
        stack.push_back(Read<U64>(data, offset));
    constexpr size_t maxStacks = 160'000;
    if (trace.stacks.size() >= maxStacks) {
        trace.invalid = true;
        return;
    }
    auto [it, isNew] = trace.uniqueStacks.try_emplace(stack, trace.stacks.size());
    if (isNew) trace.stacks.push_back(std::move(stack));
    entry.index = it->second;
}

static void CALLBACK ReadEvent(EVENT_RECORD* record) noexcept
{
    auto& trace = *static_cast<Trace*>(record->UserContext);
    if (trace.invalid) return;
    try {
        const GUID& provider = record->EventHeader.ProviderId;
        if (!trace.stackPass && IsEqualGUID(provider, HeapProvider)) ReadHeap(trace, *record);
        if (trace.stackPass && IsEqualGUID(provider, StackProvider)) ReadStack(trace, *record);
    } catch (...) {
        trace.invalid = true;
    }
}

static bool Process(wchar_t* path, Trace& trace)
{
    EVENT_TRACE_LOGFILEW log{};
    log.LogFileName = path;
    log.ProcessTraceMode = PROCESS_TRACE_MODE_EVENT_RECORD | PROCESS_TRACE_MODE_RAW_TIMESTAMP;
    log.EventRecordCallback = ReadEvent;
    log.Context = &trace;
    TRACEHANDLE handle = OpenTraceW(&log);
    if (handle == INVALID_PROCESSTRACE_HANDLE) {
        fprintf(stderr, "OpenTrace error=%lu\n", GetLastError());
        return false;
    }
    const ULONG status = ProcessTrace(&handle, 1, nullptr, nullptr);
    CloseTrace(handle);
    printf("TRACE\tpass=%u\tstatus=%lu\tfrequency=%lld\tlostEvents=%lu\tlostBuffers=%lu\tlimitOrError=%u\n",
        unsigned(trace.stackPass), status, log.LogfileHeader.PerfFreq.QuadPart, log.LogfileHeader.EventsLost,
        log.LogfileHeader.BuffersLost, unsigned(trace.invalid));
    return status == ERROR_SUCCESS && !trace.invalid && log.LogfileHeader.EventsLost == 0
        && log.LogfileHeader.BuffersLost == 0 && log.LogfileHeader.PerfFreq.QuadPart > 0;
}

static bool AtAnyPhase(const std::vector<Phase>& phases, U64 start, U64 end)
{
    const auto phase = std::lower_bound(phases.begin(), phases.end(), start,
        [](const Phase& phase, U64 value) { return phase.qpc < value; });
    return phase != phases.end() && phase->qpc < end;
}

static void MatchLifetimes(Trace& trace, const std::vector<Phase>& phases)
{
    std::stable_sort(trace.events.begin(), trace.events.end(),
        [](const HeapEvent& a, const HeapEvent& b) { return a.stack.qpc < b.stack.qpc; });
    std::unordered_map<U64, std::unordered_map<U64, HeapEvent>> heaps;
    auto close = [&](const HeapEvent& prior, const HeapEvent* endingEvent) {
        const U64 end = endingEvent ? endingEvent->stack.qpc : UINT64_MAX;
        if (!AtAnyPhase(phases, prior.stack.qpc, end)) return;
        trace.blocks.push_back({prior.stack.qpc, end, prior.size, prior.heap, prior.address, prior.stack,
            endingEvent ? endingEvent->stack : StackKey{}, endingEvent ? endingEvent->operation : Allocate});
        trace.stackKeys.try_emplace(prior.stack);
        if (endingEvent && endingEvent->operation == Release)
            trace.stackKeys.try_emplace(endingEvent->stack);
    };
    for (const auto& event : trace.events) {
        const U64 now = event.stack.qpc;
        if (now > phases.back().qpc) break;
        auto& heap = heaps[event.heap];
        auto pop = [&](U64 address) {
            auto found = heap.find(address);
            if (found == heap.end()) return false;
            if (now == found->second.stack.qpc) ++trace.cutoffConflicts;
            close(found->second, &event);
            heap.erase(found);
            return true;
        };

        switch (event.operation) {
        case Destroy:
            ++trace.destroyCount;
            for (const auto& [address, prior] : heap) close(prior, &event);
            heaps.erase(event.heap);
            continue;
        case Release:
            ++trace.releaseCount;
            if (!pop(event.address)) ++trace.missingFree;
            continue;
        case Reallocate:
            ++trace.reallocCount;
            if (!event.address) { ++trace.failedRealloc; continue; }
            if (!pop(event.oldAddress)) ++trace.missingRealloc;
            if (event.address == event.oldAddress) ++trace.sameAddress;
            else ++trace.moves;
            break;
        case Allocate:
            ++trace.allocCount;
            if (!event.address) { ++trace.failedAlloc; continue; }
            break;
        }
        if (pop(event.address)) ++trace.replaced;
        heap.emplace(event.address, event);
    }
    for (const auto& [heap, outstanding] : heaps)
        for (const auto& [address, event] : outstanding) close(event, nullptr);
    for (const auto& event : trace.events) {
        auto key = trace.stackKeys.find(event.stack);
        if (key != trace.stackKeys.end()) ++key->second.operations;
    }
    trace.events.clear();
    trace.events.shrink_to_fit();
}

static std::vector<Module> LoadModules(const wchar_t* file)
{
    std::ifstream input(file);
    if (!input) throw std::runtime_error("cannot open module inventory");
    std::vector<Module> modules;
    std::string row;
    if (!std::getline(input, row) || row.find("base") == std::string::npos
        || row.find("size") == std::string::npos || row.find("name") == std::string::npos
        || row.find("path") == std::string::npos)
        throw std::runtime_error("expected CSV headings base,size,name,path");
    while (std::getline(input, row)) {
        std::string base, size, name;
        std::istringstream record(row);
        if (!std::getline(record, base, ',') || !std::getline(record, size, ',')
            || !std::getline(record, name, ',')) continue;
        auto unquote = [](const std::string& field) {
            return field.size() >= 2 && field.front() == '"' && field.back() == '"'
                ? field.substr(1, field.size() - 2) : field;
        };
        modules.push_back({std::stoull(unquote(base), nullptr, 16),
            std::stoull(unquote(size)), unquote(name)});
    }
    if (modules.empty()) throw std::runtime_error("empty module inventory");
    std::sort(modules.begin(), modules.end(), [](const Module& a, const Module& b) { return a.base < b.base; });
    for (size_t i = 1; i < modules.size(); ++i) {
        if (modules[i].base - modules[i - 1].base < modules[i - 1].size)
            throw std::runtime_error("module bases overlap");
    }
    return modules;
}

static const Module* IdentifyModule(const std::vector<Module>& modules, U64 pc)
{
    auto after = std::upper_bound(modules.begin(), modules.end(), pc,
        [](U64 addr, const Module& module) { return addr < module.base; });
    if (after == modules.begin()) return nullptr;
    auto& candidate = *--after;
    return pc - candidate.base < candidate.size ? &candidate : nullptr;
}

static void PrintStack(const std::vector<U64>& frames, const std::vector<Module>& modules)
{
    for (U64 pc : frames) {
        const Module* module = IdentifyModule(modules, pc);
        if (module) printf("%s+0x%llx;", module->name.c_str(), pc - module->base);
        else if (pc < 0x0000800000000000ULL) printf("0x%llx;", pc);
        else printf("[kernel];");
    }
}

static std::string FirstOrigin(const std::vector<U64>& frames, const std::vector<Module>& modules)
{
    for (U64 pc : frames) {
        const Module* module = IdentifyModule(modules, pc);
        if (!module) continue;
        std::string image = module->name;
        for (char& c : image) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
        if (image == "ntdll.dll" || image == "kernelbase.dll" || image == "kernel32.dll"
            || image == "ucrtbase.dll" || image == "vcruntime140.dll") continue;
        return image;
    }
    return frames.empty() ? "no-correlated-stack" : "no-attributed-module";
}

static void PrintCohort(const Trace& trace, const std::vector<Phase>& phases,
    const std::vector<Module>& modules, const Cohort& cohort)
{
    using GroupKey = std::tuple<size_t, size_t, U64>; // birth phase, exact allocation stack, block size.
    std::vector<const Block*> targets;
    std::vector<std::string> origins;
    for (const auto& frames : trace.stacks)
        origins.push_back(FirstOrigin(frames, modules));
    if (cohort.module != "all" && std::find(origins.begin(), origins.end(), cohort.module) == origins.end())
        throw std::runtime_error("no correlated allocation stacks attributed to requested module");
    for (const Block& block : trace.blocks) {
        if (block.start > cohort.after && block.start <= cohort.through && block.end > cohort.through
            && (cohort.module == "all" || origins[trace.stackKeys.at(block.stack).index] == cohort.module))
            targets.push_back(&block);
    }

    std::vector<std::pair<GroupKey, Totals>> baseline;
    bool baselineChosen = false;
    for (const Phase& phase : phases) {
        if (phase.qpc < cohort.through) continue;
        std::map<GroupKey, Totals> groups;
        std::map<size_t, Totals> byBirth;
        std::map<std::string, Totals> byModule;
        std::map<std::pair<std::string, Operation>, Totals> byRetirement;
        Totals live{};
        for (const Block* block : targets) {
            const auto stack = trace.stackKeys.at(block->stack).index;
            const auto& origin = origins[stack];
            if (block->end <= phase.qpc) {
                byRetirement[{origin, block->endOperation}].Add(block->size);
                continue;
            }
            live.Add(block->size);
            const auto birth = std::lower_bound(phases.begin(), phases.end(), block->start,
                [](const Phase& mark, U64 start) { return mark.qpc < start; });
            const auto label = static_cast<size_t>(birth - phases.begin());
            byBirth[label].Add(block->size);
            byModule[origin].Add(block->size);
            groups[{label, stack, block->size}].Add(block->size);
        }
        printf("COHORT\t%s\t%s\t%llu\t%llu\n", cohort.module.c_str(), phase.name.c_str(), live.count, live.bytes);
        for (const auto& [label, totals] : byBirth)
            printf("COHORT_BIRTH\t%s\t%s\t%llu\t%llu\n",
                phase.name.c_str(), phases[label].name.c_str(), totals.count, totals.bytes);
        for (const auto& [module, totals] : byModule)
            printf("COHORT_MODULE\t%s\t%s\t%llu\t%llu\n", phase.name.c_str(), module.c_str(), totals.count, totals.bytes);
        for (const auto& [kind, totals] : byRetirement)
            printf("COHORT_RETIRED\t%s\t%s\t%u\t%llu\t%llu\n", phase.name.c_str(), kind.first.c_str(),
                unsigned(kind.second), totals.count, totals.bytes);
        std::vector<std::pair<GroupKey, Totals>> ranked(groups.begin(), groups.end());
        const auto keep = (std::min)(size_t{16}, ranked.size());
        std::partial_sort(ranked.begin(), ranked.begin() + keep, ranked.end(),
            [](const auto& a, const auto& b) { return a.second.bytes > b.second.bytes; });
        ranked.resize(keep);
        if (!baselineChosen) {
            baseline = ranked;
            baselineChosen = true;
        }
        if (cohort.module == "all") {
            for (const auto& [key, totals] : ranked) {
                const auto& [label, stack, size] = key;
                printf("COHORT_TOP\t%s\t%s\t%s\t%zu\t%llu\t%llu\t%llu\t", phase.name.c_str(),
                    phases[label].name.c_str(), origins[stack].c_str(), stack, size, totals.count, totals.bytes);
                PrintStack(trace.stacks[stack], modules);
                putchar('\n');
            }
        }
        for (const auto& [key, initial] : baseline) {
            const auto& [label, stack, size] = key;
            const auto& totals = groups[key];
            printf("COHORT_STACK\t%s\t%s\t%zu\t%llu\t%llu\t%llu\t%llu\t", phase.name.c_str(),
                phases[label].name.c_str(), stack, size, totals.count, totals.bytes, initial.bytes - totals.bytes);
            PrintStack(trace.stacks[stack], modules);
            putchar('\n');
        }
    }
}

static void PrintPhases(const Trace& trace, const std::vector<Phase>& phases, const std::vector<Module>& modules)
{
    U64 priorQpc = 0;
    for (const Phase& phase : phases) {
        Totals live{}, covered{}, ambiguous{}, sincePrior{}, stillLive{}, retired{};
        std::map<std::pair<U64, size_t>, Totals> groups;
        std::map<std::pair<U64, size_t>, Totals> newGroups;
        std::map<std::tuple<U64, size_t, size_t>, Totals> releasedGroups;
        std::map<std::tuple<size_t, U64, size_t>, Totals> birthGroups;
        std::map<U64, Totals> sizes;
        for (const Block& block : trace.blocks) {
            if (block.start > phase.qpc) continue;
            if (block.end <= phase.qpc) {
                if (block.start <= priorQpc && block.end > priorQpc) {
                    retired.Add(block.size);
                    if (block.endOperation == Release)
                        releasedGroups[{block.heap, trace.stackKeys.at(block.stack).index,
                            trace.stackKeys.at(block.endStack).index}].Add(block.size);
                }
                continue;
            }
            live.Add(block.size);
            sizes[block.size].Add(block.size);
            const auto& entry = trace.stackKeys.at(block.stack);
            groups[{block.heap, entry.index}].Add(block.size);
            if (block.start > priorQpc) {
                sincePrior.Add(block.size);
                newGroups[{block.heap, entry.index}].Add(block.size);
            } else stillLive.Add(block.size);
            if (entry.index) covered.Add(block.size);
            else if (entry.operations != 1 || entry.stackEvents > 1) ambiguous.Add(block.size);
            if (&phase == &phases.back()) {
                const auto birth = std::lower_bound(phases.begin(), phases.end(), block.start,
                    [](const Phase& mark, U64 start) { return mark.qpc < start; });
                birthGroups[{size_t(birth - phases.begin()), block.heap, entry.index}].Add(block.size);
            }
        }
        printf("PHASE\t%s\t%llu\t%llu\t%llu\t%llu\t%llu\t%llu\t%llu\n", phase.name.c_str(), phase.qpc,
            live.count, live.bytes, covered.count, covered.bytes, ambiguous.count, ambiguous.bytes);
        printf("FLOW\t%s\t%llu\t%llu\t%llu\t%llu\t%llu\t%llu\n", phase.name.c_str(),
            sincePrior.count, sincePrior.bytes, stillLive.count, stillLive.bytes, retired.count, retired.bytes);
        for (const auto& [key, values] : groups) {
            const auto& [heap, stackId] = key;
            const auto& added = newGroups[key];
            printf("STACK\t%s\t%llx\t%zu\t%llu\t%llu\t%llu\t%llu\t",
                phase.name.c_str(), heap, stackId, values.count, values.bytes, added.count, added.bytes);
            PrintStack(trace.stacks[stackId], modules);
            putchar('\n');
        }
        for (const auto& [size, values] : sizes)
            printf("SIZE\t%s\t%llu\t%llu\t%llu\n", phase.name.c_str(), size, values.count, values.bytes);
        for (const auto& [key, values] : releasedGroups) {
            const auto& [heap, birthStackId, releaseStackId] = key;
            printf("RELEASESTACK\t%s\t%llx\t%zu\t%zu\t%llu\t%llu\t", phase.name.c_str(),
                heap, birthStackId, releaseStackId, values.count, values.bytes);
            PrintStack(trace.stacks[releaseStackId], modules);
            putchar('\n');
        }
        for (const auto& [key, values] : birthGroups) {
            const auto& [birth, heap, stackId] = key;
            printf("BIRTH\t%s\t%s\t%llx\t%zu\t%llu\t%llu\n", phase.name.c_str(), phases[birth].name.c_str(),
                heap, stackId, values.count, values.bytes);
        }
        priorQpc = phase.qpc;
    }
}

int wmain(int argc, wchar_t** argv)
{
    try {
        if (argc < 6) throw std::runtime_error("usage: HeapCohorts trace.etl pid heapHex|all modules.csv|- phase=rawQpc [...] [--cohort module afterQpc throughQpc]");
        Trace trace;
        trace.pid = std::stoul(argv[2]);
        if (wcscmp(argv[3], L"all") != 0) trace.heap = std::stoull(argv[3], nullptr, 16);
        const auto modules = wcscmp(argv[4], L"-") == 0 ? std::vector<Module>{} : LoadModules(argv[4]);
        std::vector<Phase> phases;
        Cohort cohort{};
        for (int i = 5; i < argc; ++i) {
            std::wstring arg = argv[i];
            if (arg == L"--cohort") {
                if (i + 3 >= argc || !cohort.module.empty()) throw std::runtime_error("expected module afterQpc throughQpc");
                for (wchar_t c : std::wstring(argv[++i])) {
                    if (c > 127) throw std::runtime_error("module name must be ASCII");
                    cohort.module.push_back(static_cast<char>(std::tolower(static_cast<unsigned char>(c))));
                }
                cohort.after = std::stoull(argv[++i]);
                cohort.through = std::stoull(argv[++i]);
                if (cohort.after >= cohort.through) throw std::runtime_error("invalid cohort QPC window");
                continue;
            }
            const size_t equals = arg.find(L'=');
            if (!equals || equals == std::wstring::npos) throw std::runtime_error("expected phase=rawQpc");
            std::wstring label = arg.substr(0, equals);
            if (label.find_first_not_of(L"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_") != std::wstring::npos)
                throw std::runtime_error("phase label must be ASCII letters, digits, hyphen or underscore");
            std::string asciiLabel;
            for (wchar_t c : label) asciiLabel.push_back(static_cast<char>(c));
            phases.push_back({std::stoull(arg.substr(equals + 1)), std::move(asciiLabel)});
        }
        if (phases.empty()) throw std::runtime_error("at least one phase is required");
        if (!cohort.module.empty() && modules.empty()) throw std::runtime_error("cohort mode requires module inventory");
        std::stable_sort(phases.begin(), phases.end(), [](const Phase& a, const Phase& b) { return a.qpc < b.qpc; });
        trace.lastPhase = phases.back().qpc;
        if (!Process(argv[1], trace)) return 3;
        const auto observedEvents = trace.events.size();
        if (!observedEvents) throw std::runtime_error("no supported events for target PID and heap");
        MatchLifetimes(trace, phases);
        trace.stackPass = true;
        if (!Process(argv[1], trace)) return 4;
        if (std::none_of(trace.stackKeys.begin(), trace.stackKeys.end(), [](const auto& entry) { return entry.second.index != 0; }))
            throw std::runtime_error("no phase allocations with matching raw-QPC stacks; check PID, clock, stackwalk and phase boundaries");
        printf("MATCH\tobserved=%zu\talloc=%llu\trealloc=%llu\tfree=%llu\tunknownFree=%llu\tunknownRealloc=%llu\tfailedAlloc=%llu"
            "\tfailedRealloc=%llu\tmoved=%llu\tinPlace=%llu\treplaced=%llu\tdestroyedHeaps=%llu\tsameAddressQpcTies=%llu\tmalformed=%llu\n",
            observedEvents, trace.allocCount, trace.reallocCount, trace.releaseCount, trace.missingFree, trace.missingRealloc,
            trace.failedAlloc, trace.failedRealloc, trace.moves, trace.sameAddress, trace.replaced, trace.destroyCount,
            trace.cutoffConflicts, trace.malformed);
        if (cohort.module.empty()) PrintPhases(trace, phases, modules);
        else PrintCohort(trace, phases, modules, cohort);
        return trace.malformed ? 5 : 0;
    } catch (const std::exception& error) {
        fprintf(stderr, "%s\n", error.what());
        return 2;
    }
}
