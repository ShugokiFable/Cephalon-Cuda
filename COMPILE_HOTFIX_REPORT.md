# Cephalon Cuda 2.2 compiler correction report

## Windows build findings resolved

The second real Windows warnings-as-errors build exposed 50 diagnostics. They reduced to five root causes:

1. `System.IO` types were used without explicit imports in three services.
2. Two URL checks used property values inside constant patterns, producing `CS9135`.
3. `ReturnPlannerService` dereferenced a nullable session ID, producing `CS8629`.
4. The onboarding roadmap declared `Url` as non-nullable while multiple seeded rows intentionally used `null`, producing repeated `CS8619` errors.
5. The earlier `System.Net.Http` import correction remains included.

## Applied corrections

- Added `using System.IO;` to `DataIntelligenceService`, `MarketService`, and `OnboardingService`.
- Replaced the invalid URI scheme patterns with explicit ordinal-ignore-case comparisons.
- Pattern-matched `_activeSessionId` into a non-null `long` before database use.
- Changed the roadmap tuple field to `string? Url`.
- Expanded the offline validator with regression checks for every root cause above.

## Remaining gate

Run `BUILD_V2.2.bat` on Windows again. These changes resolve all 50 diagnostics in the supplied build log. A further real compiler pass is still the authoritative check for any later diagnostics that were previously masked.
