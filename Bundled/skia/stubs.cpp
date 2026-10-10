// Bodies for entry points the linked C API objects reference but this raster-only
// build never compiles (Ganesh images, animated WebP, path ops: //:pathops would
// link every :core object as a loose object). No export reaches them,
// so dead-stripping drops them; they exist because lld-link resolves every symbol
// of a linked object before it discards dead code.

#include "include/core/SkColorSpace.h"
#include "include/core/SkImage.h"
#include "include/encode/SkWebpEncoder.h"
#include "include/gpu/ganesh/SkImageGanesh.h"
#include "include/pathops/SkPathOps.h"
#include "include/private/SkAssert.h"

namespace SkImages {
sk_sp<SkImage> AdoptTextureFrom(GrRecordingContext*, const GrBackendTexture&, GrSurfaceOrigin,
                                SkColorType, SkAlphaType, sk_sp<SkColorSpace>) {
    SK_ABORT("raster-only build");
}
sk_sp<SkImage> BorrowTextureFrom(GrRecordingContext*, const GrBackendTexture&, GrSurfaceOrigin,
                                 SkColorType, SkAlphaType, sk_sp<SkColorSpace>,
                                 TextureReleaseProc, ReleaseContext) {
    SK_ABORT("raster-only build");
}
sk_sp<SkImage> TextureFromImage(GrDirectContext*, const SkImage*, skgpu::Mipmapped, skgpu::Budgeted) {
    SK_ABORT("raster-only build");
}
sk_sp<SkImage> MakeWithFilter(GrRecordingContext*, sk_sp<SkImage>, const SkImageFilter*,
                              const SkIRect&, const SkIRect&, SkIRect*, SkIPoint*) {
    SK_ABORT("raster-only build");
}
}  // namespace SkImages

namespace SkWebpEncoder {
bool EncodeAnimated(SkWStream*, SkSpan<const SkEncoder::Frame>, const Options&) {
    SK_ABORT("raster-only build");
}
}  // namespace SkWebpEncoder

std::optional<SkPath> Op(const SkPath&, const SkPath&, SkPathOp) { SK_ABORT("raster-only build"); }
std::optional<SkPath> Simplify(const SkPath&) { SK_ABORT("raster-only build"); }
std::optional<SkPath> AsWinding(const SkPath&) { SK_ABORT("raster-only build"); }
void SkOpBuilder::add(const SkPath&, SkPathOp) { SK_ABORT("raster-only build"); }
std::optional<SkPath> SkOpBuilder::resolve() { SK_ABORT("raster-only build"); }
