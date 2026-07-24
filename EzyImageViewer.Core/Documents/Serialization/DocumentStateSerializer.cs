using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;

namespace EzyImageViewer.Core.Documents.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(DocumentStateDto))]
public sealed partial class DocumentSerializationContext : JsonSerializerContext;

/// <summary>
/// 문서 상태와 저장용 조각을 변환. 읽기는 불신 경계라 알 수 없는 값·누락·중복·과다 입력을
/// <see cref="InvalidDataException"/>으로 한결같이 거절.
/// </summary>
public static class DocumentStateSerializer
{
    /// <summary>악성 입력이 작업·주석 목록을 부풀리지 못하게 양방향 상한 적용.</summary>
    public const int MaxOps = 10_000;
    public const int MaxAnnotations = 10_000;
    public const int MaxLayers = AnnotationValidator.MaxLayers;
    public const int MaxAssets = 1_024;

    /// <summary>DTO를 만들기 전에 막는 원문 크기 상한.</summary>
    public const int MaxJsonChars = 96 * 1024 * 1024;

    /// <summary>v2 레이어 형식 작성. 활성 레이어 힌트는 실제 레이어를 가리켜야 함.</summary>
    public static string Write(DocumentState state, Guid? activeLayerId = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Transform.Ops.Count > MaxOps)
            throw new InvalidDataException($"Document state exceeds the {MaxOps} transform op limit.");
        if (state.Layers.Count is 0 or > MaxLayers)
            throw new InvalidDataException($"Document state exceeds the {MaxLayers} layer limit.");
        var flat = state.Annotations;
        if (flat.Count > MaxAnnotations)
            throw new InvalidDataException($"Document state exceeds the {MaxAnnotations} annotation limit.");
        if (state.Assets.Count > MaxAssets)
            throw new InvalidDataException($"Document state exceeds the {MaxAssets} raster asset limit.");
        if (activeLayerId is { } active && state.FindLayer(active) is null)
            throw new InvalidDataException($"Active layer {active} is not in the document.");
        var referencedAssetIds = flat.OfType<ImageAnnotation>()
            .Select(image => image.AssetId).ToHashSet();
        var referencedAssets = state.Assets.Where(asset => referencedAssetIds.Contains(asset.Id)).ToList();
        try
        {
            foreach (var layer in state.Layers)
                AnnotationValidator.Validate(layer);
            foreach (var annotation in flat)
            {
                AnnotationValidator.Validate(annotation);
                if (annotation is ImageAnnotation image && state.FindAsset(image.AssetId) is null)
                    throw new InvalidOperationException($"Image annotation references missing asset {image.AssetId}.");
            }
            foreach (var asset in referencedAssets)
                AnnotationValidator.Validate(asset);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or NotSupportedException or OverflowException)
        {
            throw new InvalidDataException("Document state contains an invalid annotation.", ex);
        }
        var dto = new DocumentStateDto
        {
            Transform = [.. state.Transform.Ops.Select(ToDto)],
            Layers = [.. state.Layers.Select(ToDto)],
            ActiveLayerId = activeLayerId,
            Assets = [.. referencedAssets.Select(ToDto)],
        };
        return JsonSerializer.Serialize(dto, DocumentSerializationContext.Default.DocumentStateDto);
    }

    public static DocumentState Read(string json) => Read(json, declaredSchemaVersion: null, out _);

    /// <summary>컨테이너 스키마와 조각 형식 일치 확인. v1은 평면 주석, v2는 레이어.</summary>
    public static DocumentState Read(string json, int schemaVersion) =>
        Read(json, schemaVersion, out _);

    /// <summary>검증된 활성 레이어 힌트도 반환. v1이거나 없으면 null.</summary>
    public static DocumentState Read(string json, int schemaVersion, out Guid? activeLayerId)
    {
        if (schemaVersion is < 1 or > ProjectManifest.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported document schema version {schemaVersion}.");
        return Read(json, declaredSchemaVersion: schemaVersion, out activeLayerId);
    }

    private static DocumentState Read(string json, int? declaredSchemaVersion, out Guid? activeLayerId)
    {
        activeLayerId = null;
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxJsonChars)
            throw new InvalidDataException($"Document fragment exceeds the {MaxJsonChars:N0} character limit.");
        DocumentStateDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, DocumentSerializationContext.Default.DocumentStateDto);
        }
        // 다형성 구분자 누락 예외도 이 경계에서는 똑같이 잘못된 데이터.
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("Document fragment is malformed.", ex);
        }
        if (dto is null)
            throw new InvalidDataException("Document fragment is empty.");
        // required는 존재만 확인하므로 참조형의 명시적 null은 따로 검사.
        if (dto.Transform is null)
            throw new InvalidDataException("Document fragment has null sections.");
        // 버전이 곧 모양. v1은 평면 목록, v2는 레이어이며 둘 다거나 둘 다 없음은 금지.
        if (dto.Annotations is null == dto.Layers is null)
            throw new InvalidDataException("Document fragment must have exactly one of annotations or layers.");
        if (dto.Layers is null && dto.ActiveLayerId is not null)
            throw new InvalidDataException("Document fragment has an active layer without layers.");
        var shapeVersion = dto.Annotations is not null ? 1 : 2;
        if (declaredSchemaVersion is { } declared && declared != shapeVersion)
            throw new InvalidDataException(
                $"Document fragment shape is v{shapeVersion} but the manifest declares schema {declared}.");
        if (dto.Transform.Count > MaxOps)
            throw new InvalidDataException($"Document fragment exceeds the {MaxOps} transform op limit.");
        if (dto.Layers is { Count: var layerCount } && layerCount is 0 or > MaxLayers)
            throw new InvalidDataException($"Document fragment must have 1..{MaxLayers} layers.");
        var totalAnnotations = dto.Annotations?.Count
            ?? dto.Layers!.Sum(layer => layer?.Annotations?.Count ?? 0);
        if (totalAnnotations > MaxAnnotations)
            throw new InvalidDataException($"Document fragment exceeds the {MaxAnnotations} annotation limit.");
        if ((dto.Assets?.Count ?? 0) > MaxAssets)
            throw new InvalidDataException($"Document fragment exceeds the {MaxAssets} raster asset limit.");

        try
        {
            var transform = BackgroundTransform.Identity;
            foreach (var op in dto.Transform)
            {
                // JSON null 요소는 예외가 아니라 null 목록 항목으로 들어옴.
                if (op is null)
                    throw new InvalidDataException("Document fragment has a null transform op.");
                transform = transform.Append(ToDomain(op));
            }

            var assets = new List<RasterAsset>(dto.Assets?.Count ?? 0);
            var seenAssets = new HashSet<Guid>();
            foreach (var asset in dto.Assets ?? [])
            {
                if (asset is null || asset.Id == Guid.Empty || !seenAssets.Add(asset.Id))
                    throw new InvalidDataException("Document fragment has a null, empty, or duplicate raster asset.");
                assets.Add(ToDomain(asset));
            }

            var state = new DocumentState { Transform = transform };
            foreach (var asset in assets)
                state = state.AddAsset(asset);

            var seen = new HashSet<Guid>();
            if (dto.Annotations is { } flat)
            {
                // v1 승격: 평면 목록을 순서 그대로 초기 레이어 하나에 담음.
                foreach (var annotation in flat)
                    state = state.AddAnnotation(ToDomainChecked(annotation, seen));
                return state;
            }

            var seenLayers = new HashSet<Guid>();
            var layers = new List<AnnotationLayer>(dto.Layers!.Count);
            foreach (var layerDto in dto.Layers)
            {
                if (layerDto is null || layerDto.Id == Guid.Empty || !seenLayers.Add(layerDto.Id))
                    throw new InvalidDataException("Document fragment has a null, empty, or duplicate layer id.");
                if (layerDto.Annotations is null)
                    throw new InvalidDataException("Document fragment has a layer with a null annotation list.");
                var items = new List<Annotation>(layerDto.Annotations.Count);
                foreach (var annotation in layerDto.Annotations)
                {
                    var domain = ToDomainChecked(annotation, seen);
                    if (domain is ImageAnnotation image && !seenAssets.Contains(image.AssetId))
                        throw new InvalidDataException($"Image annotation references missing asset {image.AssetId}.");
                    items.Add(domain);
                }
                layers.Add(AnnotationValidator.Validate(new AnnotationLayer
                {
                    Id = layerDto.Id,
                    Name = layerDto.Name ?? "",
                    IsVisible = layerDto.IsVisible ?? true,
                    IsLocked = layerDto.IsLocked ?? false,
                    Annotations = items,
                }));
            }
            if (dto.ActiveLayerId is { } active && !seenLayers.Contains(active))
                throw new InvalidDataException($"Active layer {active} is not in the document.");
            activeLayerId = dto.ActiveLayerId;
            // 고유성·상한·자산 참조는 위에서 검증 완료.
            return state with { Layers = layers };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or NotSupportedException or OverflowException)
        {
            // 도메인 생성자가 유한값·양수·상한의 단일 검증자. 여기서 실패하면 잘못된 데이터.
            throw new InvalidDataException("Document fragment contains invalid values.", ex);
        }
    }

    private static Annotation ToDomainChecked(AnnotationDto? annotation, HashSet<Guid> seenIds)
    {
        if (annotation is null)
            throw new InvalidDataException("Document fragment has a null annotation.");
        if (annotation.Id == Guid.Empty || !seenIds.Add(annotation.Id))
            throw new InvalidDataException($"Document fragment has an empty or duplicate annotation id '{annotation.Id}'.");
        return ToDomain(annotation);
    }

    private static TransformOpDto ToDto(TransformOp op) => op switch
    {
        CropOp crop => new CropOpDto
        {
            X = crop.Bounds.X,
            Y = crop.Bounds.Y,
            Width = crop.Bounds.Width,
            Height = crop.Bounds.Height,
        },
        RotateOp rotate => new RotateOpDto { Degrees = rotate.Degrees },
        FlipOp flip => new FlipOpDto { Horizontal = flip.Horizontal },
        ResizeOp resize => new ResizeOpDto { Width = resize.Target.Width, Height = resize.Target.Height },
        EraseOp erase => new EraseOpDto
        {
            X = erase.Bounds.X,
            Y = erase.Bounds.Y,
            Width = erase.Bounds.Width,
            Height = erase.Bounds.Height,
        },
        _ => throw new NotSupportedException($"Unknown transform op {op.GetType().Name}."),
    };

    private static TransformOp ToDomain(TransformOpDto dto) => dto switch
    {
        CropOpDto crop => new CropOp(new RectF(crop.X, crop.Y, crop.Width, crop.Height)),
        RotateOpDto rotate => new RotateOp(rotate.Degrees),
        FlipOpDto flip => new FlipOp(flip.Horizontal),
        ResizeOpDto resize => new ResizeOp(new PixelSize(resize.Width, resize.Height)),
        EraseOpDto erase => new EraseOp(new RectF(erase.X, erase.Y, erase.Width, erase.Height)),
        _ => throw new NotSupportedException($"Unknown transform op dto {dto.GetType().Name}."),
    };

    private static AnnotationDto ToDto(Annotation annotation) => annotation switch
    {
        RectangleAnnotation rectangle => new RectangleAnnotationDto
        {
            Id = rectangle.Id,
            Name = rectangle.Name,
            IsVisible = rectangle.IsVisible,
            IsLocked = rectangle.IsLocked,
            RotationDegrees = rectangle.RotationDegrees,
            X = rectangle.Bounds.X,
            Y = rectangle.Bounds.Y,
            Width = rectangle.Bounds.Width,
            Height = rectangle.Bounds.Height,
            StrokeArgb = rectangle.StrokeArgb,
            StrokeWidth = rectangle.StrokeWidth,
            Shape = (int)rectangle.Shape,
            FillArgb = rectangle.FillArgb,
            CornerRadius = rectangle.CornerRadius,
            Opacity = rectangle.Opacity,
        },
        InkAnnotation ink => new InkAnnotationDto
        {
            Id = ink.Id,
            Name = ink.Name,
            IsVisible = ink.IsVisible,
            IsLocked = ink.IsLocked,
            RotationDegrees = ink.RotationDegrees,
            Points = [.. ink.Points.Select(ToDto)],
            InkKind = (int)ink.Kind,
            StrokeArgb = ink.StrokeArgb,
            StrokeWidth = ink.StrokeWidth,
            Opacity = ink.Opacity,
        },
        LineAnnotation line => new LineAnnotationDto
        {
            Id = line.Id,
            Name = line.Name,
            IsVisible = line.IsVisible,
            IsLocked = line.IsLocked,
            RotationDegrees = line.RotationDegrees,
            Start = ToDto(line.Start),
            End = ToDto(line.End),
            StartArrowhead = (int)line.StartArrowhead,
            EndArrowhead = (int)line.EndArrowhead,
            StrokeArgb = line.StrokeArgb,
            StrokeWidth = line.StrokeWidth,
            Opacity = line.Opacity,
        },
        TextAnnotation text => new TextAnnotationDto
        {
            Id = text.Id,
            Name = text.Name,
            IsVisible = text.IsVisible,
            IsLocked = text.IsLocked,
            RotationDegrees = text.RotationDegrees,
            X = text.Bounds.X,
            Y = text.Bounds.Y,
            Width = text.Bounds.Width,
            Height = text.Bounds.Height,
            Text = text.Text,
            FontFamily = text.FontFamily,
            FontSize = text.FontSize,
            IsBold = text.IsBold,
            IsItalic = text.IsItalic,
            ForegroundArgb = text.ForegroundArgb,
            BackgroundArgb = text.BackgroundArgb,
            Alignment = (int)text.Alignment,
            Opacity = text.Opacity,
        },
        SpeechBubbleAnnotation bubble => new SpeechBubbleAnnotationDto
        {
            Id = bubble.Id,
            Name = bubble.Name,
            IsVisible = bubble.IsVisible,
            IsLocked = bubble.IsLocked,
            RotationDegrees = bubble.RotationDegrees,
            X = bubble.Bounds.X,
            Y = bubble.Bounds.Y,
            Width = bubble.Bounds.Width,
            Height = bubble.Bounds.Height,
            TailX = bubble.TailTip.X,
            TailY = bubble.TailTip.Y,
            Text = bubble.Text,
            FontFamily = bubble.FontFamily,
            FontSize = bubble.FontSize,
            IsBold = bubble.IsBold,
            IsItalic = bubble.IsItalic,
            ForegroundArgb = bubble.ForegroundArgb,
            Alignment = (int)bubble.Alignment,
            FillArgb = bubble.FillArgb,
            StrokeArgb = bubble.StrokeArgb,
            StrokeWidth = bubble.StrokeWidth,
            CornerRadius = bubble.CornerRadius,
            Opacity = bubble.Opacity,
        },
        NumberMarkerAnnotation marker => new NumberMarkerAnnotationDto
        {
            Id = marker.Id,
            Name = marker.Name,
            IsVisible = marker.IsVisible,
            IsLocked = marker.IsLocked,
            RotationDegrees = marker.RotationDegrees,
            X = marker.Bounds.X,
            Y = marker.Bounds.Y,
            Width = marker.Bounds.Width,
            Height = marker.Bounds.Height,
            Number = marker.Number,
            FillArgb = marker.FillArgb,
            ForegroundArgb = marker.ForegroundArgb,
            FontSize = marker.FontSize,
            Opacity = marker.Opacity,
        },
        ImageAnnotation image => new ImageAnnotationDto
        {
            Id = image.Id,
            Name = image.Name,
            IsVisible = image.IsVisible,
            IsLocked = image.IsLocked,
            RotationDegrees = image.RotationDegrees,
            X = image.Bounds.X,
            Y = image.Bounds.Y,
            Width = image.Bounds.Width,
            Height = image.Bounds.Height,
            AssetId = image.AssetId,
            Opacity = image.Opacity,
        },
        ProtectionAnnotation protection => new ProtectionAnnotationDto
        {
            Id = protection.Id,
            Name = protection.Name,
            IsVisible = protection.IsVisible,
            IsLocked = protection.IsLocked,
            RotationDegrees = protection.RotationDegrees,
            X = protection.Bounds.X,
            Y = protection.Bounds.Y,
            Width = protection.Bounds.Width,
            Height = protection.Bounds.Height,
            ProtectionKind = (int)protection.Kind,
            BlockSize = protection.BlockSize,
            BlurSigma = protection.BlurSigma,
            MaskArgb = protection.MaskArgb,
        },
        _ => throw new NotSupportedException($"Unknown annotation {annotation.GetType().Name}."),
    };

    private static Annotation ToDomain(AnnotationDto dto)
    {
        Annotation annotation = dto switch
        {
            RectangleAnnotationDto rectangle => new RectangleAnnotation
            {
                Id = rectangle.Id,
                Bounds = new RectF(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height),
                Shape = (ShapeKind)(rectangle.Shape ?? (int)ShapeKind.Rectangle),
                StrokeArgb = rectangle.StrokeArgb,
                StrokeWidth = rectangle.StrokeWidth,
                FillArgb = rectangle.FillArgb,
                CornerRadius = rectangle.CornerRadius ?? 8f,
                Opacity = rectangle.Opacity ?? 1f,
            },
            InkAnnotationDto ink => new InkAnnotation
            {
                Id = ink.Id,
                Points = ToPoints(ink.Points),
                Kind = (InkKind)ink.InkKind,
                StrokeArgb = ink.StrokeArgb,
                StrokeWidth = ink.StrokeWidth,
                Opacity = ink.Opacity,
            },
            LineAnnotationDto line => new LineAnnotation
            {
                Id = line.Id,
                Start = ToPoint(line.Start),
                End = ToPoint(line.End),
                StartArrowhead = (ArrowheadKind)line.StartArrowhead,
                EndArrowhead = (ArrowheadKind)line.EndArrowhead,
                StrokeArgb = line.StrokeArgb,
                StrokeWidth = line.StrokeWidth,
                Opacity = line.Opacity,
            },
            TextAnnotationDto text => new TextAnnotation
            {
                Id = text.Id,
                Bounds = new RectF(text.X, text.Y, text.Width, text.Height),
                Text = text.Text ?? throw new InvalidDataException("Text annotation has null text."),
                FontFamily = text.FontFamily
                    ?? throw new InvalidDataException("Text annotation has null font family."),
                FontSize = text.FontSize,
                IsBold = text.IsBold,
                IsItalic = text.IsItalic,
                ForegroundArgb = text.ForegroundArgb,
                BackgroundArgb = text.BackgroundArgb,
                Alignment = (AnnotationTextAlignment)text.Alignment,
                Opacity = text.Opacity,
            },
            SpeechBubbleAnnotationDto bubble => new SpeechBubbleAnnotation
            {
                Id = bubble.Id,
                Bounds = new RectF(bubble.X, bubble.Y, bubble.Width, bubble.Height),
                TailTip = new AnnotationPoint(bubble.TailX, bubble.TailY),
                Text = bubble.Text
                    ?? throw new InvalidDataException("Speech bubble has null text."),
                FontFamily = bubble.FontFamily
                    ?? throw new InvalidDataException("Speech bubble has null font family."),
                FontSize = bubble.FontSize,
                IsBold = bubble.IsBold,
                IsItalic = bubble.IsItalic,
                ForegroundArgb = bubble.ForegroundArgb,
                Alignment = (AnnotationTextAlignment)bubble.Alignment,
                FillArgb = bubble.FillArgb,
                StrokeArgb = bubble.StrokeArgb,
                StrokeWidth = bubble.StrokeWidth,
                CornerRadius = bubble.CornerRadius,
                Opacity = bubble.Opacity,
            },
            NumberMarkerAnnotationDto marker => new NumberMarkerAnnotation
            {
                Id = marker.Id,
                Bounds = new RectF(marker.X, marker.Y, marker.Width, marker.Height),
                Number = marker.Number,
                FillArgb = marker.FillArgb,
                ForegroundArgb = marker.ForegroundArgb,
                FontSize = marker.FontSize,
                Opacity = marker.Opacity,
            },
            ImageAnnotationDto image => new ImageAnnotation
            {
                Id = image.Id,
                Name = image.Name,
                IsVisible = image.IsVisible ?? true,
                IsLocked = image.IsLocked ?? false,
                RotationDegrees = image.RotationDegrees ?? 0f,
                Bounds = new RectF(image.X, image.Y, image.Width, image.Height),
                AssetId = image.AssetId,
                Opacity = image.Opacity,
            },
            ProtectionAnnotationDto protection => new ProtectionAnnotation
            {
                Id = protection.Id,
                Bounds = new RectF(protection.X, protection.Y, protection.Width, protection.Height),
                Kind = (ProtectionKind)protection.ProtectionKind,
                BlockSize = protection.BlockSize,
                BlurSigma = protection.BlurSigma,
                MaskArgb = protection.MaskArgb,
            },
            _ => throw new NotSupportedException($"Unknown annotation dto {dto.GetType().Name}."),
        };

        annotation = annotation with
        {
            Name = dto.Name,
            IsVisible = dto.IsVisible ?? true,
            IsLocked = dto.IsLocked ?? false,
            RotationDegrees = dto.RotationDegrees ?? 0f,
        };
        return AnnotationValidator.Validate(annotation);
    }

    private static AnnotationLayerDto ToDto(AnnotationLayer layer) => new()
    {
        Id = layer.Id,
        Name = layer.Name.Length == 0 ? null : layer.Name,
        IsVisible = layer.IsVisible,
        IsLocked = layer.IsLocked,
        Annotations = [.. layer.Annotations.Select(ToDto)],
    };

    private static AnnotationPointDto ToDto(AnnotationPoint point) =>
        new() { X = point.X, Y = point.Y };

    private static RasterAssetDto ToDto(RasterAsset asset) => new()
    {
        Id = asset.Id,
        EncodedBytes = asset.EncodedBytes.ToArray(),
        Width = asset.PixelSize.Width,
        Height = asset.PixelSize.Height,
        Format = asset.Format,
    };

    private static RasterAsset ToDomain(RasterAssetDto asset)
    {
        if (asset.EncodedBytes is null || asset.Format is null)
            throw new InvalidDataException("Raster asset has null fields.");
        return AnnotationValidator.Validate(new RasterAsset
        {
            Id = asset.Id,
            EncodedBytes = asset.EncodedBytes.ToImmutableArray(),
            PixelSize = new PixelSize(asset.Width, asset.Height),
            Format = asset.Format,
        });
    }

    private static AnnotationPoint ToPoint(AnnotationPointDto? point)
    {
        if (point is null)
            throw new InvalidDataException("Annotation has a null point.");
        return new AnnotationPoint(point.X, point.Y);
    }

    private static ImmutableArray<AnnotationPoint> ToPoints(List<AnnotationPointDto>? points)
    {
        if (points is null)
            throw new InvalidDataException("Ink annotation has a null point list.");
        if (points.Count > AnnotationValidator.MaxInkPoints)
            throw new InvalidDataException(
                $"Ink annotation exceeds {AnnotationValidator.MaxInkPoints} points.");
        return points.Select(ToPoint).ToImmutableArray();
    }
}
