# Stage 04: MULTILEADER (MLEADER)

## Overview

MULTILEADER (MLEADER) is one of the most complex annotation entities in DXF. It combines leader lines (arrows pointing to features) with annotation content (either MTEXT or a block reference). Unlike DIMENSION entities, MULTILEADERs do NOT have pre-rendered anonymous blocks -- the renderer must compute all geometry from the entity's data.

The MLEADER entity has been called the "worst-documented DXF entity" because its DXF representation is essentially a direct serialization of AutoCAD's internal `AcDbMLeader` C++ class structure, exposing implementation details rather than a clean data format. Understanding the data layout requires careful study of the group code sequences and their nesting.

An MLEADER can have multiple leader nodes, each leader node can have multiple leader lines, and each leader line is a sequence of vertices. The "dogleg" is a horizontal segment connecting the last leader vertex to the content. A landing gap provides spacing between the dogleg and the content.

The target module for this stage is `MLeaderDecomposer.cs`.

> **Key reference**: The article ["Demystifying DXF: LEADER and MULTILEADER"](https://atlight.github.io/formats/dxf-leader.html) by Alan Thomas (ThinkSpatial, 2018) is the most detailed public documentation of the MLEADER DXF format. A local copy is saved at `specs/dxf-leader-and-multileader-atlight.md`.

---

## Domain Knowledge

### DXF Section Structure (Parsing)

The MULTILEADER entity in DXF is divided into nested sections using 30x group codes as delimiters. Group codes have **different meanings depending on which section they appear in** (the DXF spec correctly documents this aspect):

```
...              // common group codes
300
CONTEXT_DATA{
  ...            // context data group codes
  302
  LEADER{
    ...          // leader group codes ("Leader Node" in DXF spec)
    304
    LEADER_LINE{
      ...        // leader line group codes
    305
    }
    304
    LEADER_LINE{
      ...
    305
    }
    ...          // further leader group codes
  303
  }
  302
  LEADER{
    ...          // leader group codes with leader line section(s)
  303
  }
  ...            // further context data group codes
301
}
...              // further common group codes
```

Zero or more LEADER and LEADER_LINE sections may exist. Note that:
- The DXF spec refers to the entity as "MLEADER" but AutoCAD's DXF writer outputs the entity type name as "MULTILEADER"
- ACadSharp handles parsing of these sections, so the C# code should work with ACadSharp's object model rather than raw group codes

### Handle-Based References

Unlike most other DXF entities which reference objects by name, MULTILEADER entities reference other DXF objects **by handle**. Even text styles and ATTDEF entities (always referenced by name in other entities) use handle references here. Key handle group codes:
- Common section: 342 (text style), 344 (block record), 330 (ATTDEF handles)
- Context data section: 340 (MLeaderStyle), 341 (block record)

### MLEADER Data Structure

The MLEADER entity has a hierarchical structure:

```
MLEADER
  +-- MLeaderStyle (referenced by handle, group 340)
  +-- PropertyOverrideFlags (group 90, bitmask indicating which properties override the style)
  +-- CONTEXT_DATA
  |     +-- Content scale (40)
  |     +-- Content base point (10/20/30)
  |     +-- Text direction (11/21/31)
  |     +-- Content rotation (41, radians)
  |     +-- Boundary width/height (42/43)
  |     +-- Flow direction (44)
  |     +-- For MTEXT content:
  |     |     +-- Default text (304)
  |     |     +-- Text location (12/22/32)
  |     |     +-- Text direction (13/23/33)
  |     |     +-- Text rotation (42)
  |     |     +-- Text width (44)
  |     |     +-- Text height (45)
  |     |     +-- Text line spacing factor (46)
  |     |     +-- Text attachment type (174)
  |     |     +-- Text color (93)
  |     |     +-- Text style handle (340)
  |     +-- For BLOCK content:
  |     |     +-- Block record handle (341)
  |     |     +-- Block scale (15/25/35)
  |     |     +-- Block rotation (46, radians)
  |     |     +-- Block color (93)
  |     |     +-- Block transform matrix (16 doubles)
  |     +-- LEADER nodes (one or more)
  |           +-- Has set last leader line flag (290)
  |           +-- Has set dogleg vector flag (291)
  |           +-- Last leader line point (10/20/30)
  |           +-- Dogleg vector (11/21/31)
  |           +-- Break start/end points (12/22/32, 13/23/33)
  |           +-- Leader branch index (90)
  |           +-- Dogleg length (40)
  |           +-- LEADER_LINE entries (one or more per node)
  |                 +-- Vertices (10/20/30, repeated for each vertex)
  |                 +-- Break start/end indices (90)
  |                 +-- Leader line index (91)
  |                 +-- Arrow head handle (340)
  |                 +-- Arrow head size (40)
```

### MLeader Style Properties

The MLEADERSTYLE table entry defines defaults:

| Property | Description |
|----------|-------------|
| Content type | 0 = None, 1 = Block, 2 = MTEXT |
| Leader type | 0 = Invisible, 1 = Straight, 2 = Spline |
| Leader line color | Color of leader lines |
| Leader line type | Linetype of leader lines |
| Leader line weight | Lineweight of leader lines |
| Arrowhead | Arrow block handle and size |
| Text style | Handle to STYLE table entry |
| Text color | Color for MTEXT content |
| Text height | Default text height |
| Text attachment direction | 0 = Horizontal, 1 = Vertical |
| Text left/right attachment | Attachment type for left/right leaders |
| Content connection type | How content connects to leader |
| Enable dogleg | Whether dogleg is drawn |
| Dogleg length | Default dogleg length |
| Landing gap | Gap between dogleg end and content |
| Enable landing | Whether landing (connection) is drawn |
| Scale | Annotative scale factor |
| Block record | Handle of block for block content type |

### Leader Lines and Vertices

Each leader line is a polyline defined by WCS vertices. Three vertex types exist in a MULTILEADER:

1. **Ordinary vertices** -- Comprise the leader line, given by (10,20,30) group codes in the LEADER_LINE section
2. **Landing point** -- Given by (10,20,30) group codes in the LEADER section (the leader node level)
3. **Dogleg endpoint** -- Must be **calculated** (not stored): `landing_point + dogleg_length * dogleg_direction_vector`

Common group code 170 determines rendering type:
- **Straight (1)** -- Join vertices of each leader line with the dogleg, interrupting lines at breaks
- **Spline (2)** -- Vertices are treated as **equally-weighted fit points** (NOT control points) of a degree 3 spline; periodic but not planar or closed; fit tolerance is 0; start/end tangent directions follow first/last line segments
- **None (0)** -- Leaders don't render; only content (text or block) displays

### Dogleg

The dogleg is a segment at the end of the leader that connects to the content. AutoCAD UI calls it the "landing."

```
Leader vertices → ... → last vertex → landing point → dogleg endpoint → content
```

**Dogleg endpoint calculation**: `landing_point + dogleg_length * dogleg_direction_vector`

**A dogleg draws** for a particular leader within the MULTILEADER if ALL of these conditions are met:
1. MULTILEADER has doglegs enabled (common group code 291 is nonzero) **AND**
2. MULTILEADER is straight (common group code 170 is 1) **AND**
3. Dogleg length (leader group code 40) is nonzero **AND**
4. Dogleg direction vector (leader group codes 11,21,31) is not a zero vector

**Critical**: Even when the dogleg doesn't draw, the dogleg endpoint must still be calculated because **the landing point is ignored** -- leader lines always terminate at the dogleg endpoint, not the landing point.

### Leader Line Breaks (DIMBREAK)

Straight MULTILEADER leader lines can be "broken" to avoid intersections with other linework. Each break is stored as a point pair (gap start, gap end).

**Breaks between vertices** _n_ and _n+1_ are stored after vertex _n_ in the LEADER_LINE section:
- Leader line group codes (11,21,31) = break start point
- Leader line group codes (12,22,32) = break end point
- The 11,21,31,12,22,32 sequence repeats for each break in that segment

**Breaks in the dogleg** are stored in the LEADER section:
- Leader group codes (12,22,32) = break start point
- Leader group codes (13,23,33) = break end point
- Repeats as needed

Spline MULTILEADERs do not use breaks.

### Landing Gap

The landing gap is a small space between the end of the dogleg and the beginning of the content. It prevents the leader line from touching the text or block directly.

```
... dogleg_end --[gap]--> content_start
```

### Arrowhead

Each leader line has an arrowhead at its first vertex (the tip). The arrow:
- Can be the default closed filled arrow or a custom block (referenced by handle)
- Size comes from style or entity/line override
- Direction: along the first segment of the leader line (from first vertex toward second vertex)
- For spline leaders, the arrowhead's back point is the final fit point, but this isn't given in DXF data; implementations must calculate or approximate it

### Content Types

**MTEXT content** (content type 2, common group code 172):
- Internally represented as an `AcDbMText` data member of the `AcDbMLeader` class -- implementations can share code between MTEXT and MLEADER text rendering
- Text anchors at the point given by context data group codes (12,22,32), corresponding to MTEXT group codes (10,20,30)
- Uses the text style, height, color, and attachment from CONTEXT_DATA
- **Important**: Where the same value appears in both common and context data sections (e.g., text color), AutoCAD uses the **context data section** value
- Rendered using the TextLayoutEngine from Stage 02

**BLOCK content** (content type 1, common group code 172):
- Common group code 344 stores the BLOCK_RECORD handle
- Insert block at the position given by context data group codes (15,25,35)
- Context data parameters (block normal direction, block scale, block rotation) interpret as for INSERT
- Group code 47 appears 16 times providing a 3D affine transformation matrix (last 4 values are always zero). This matrix can be **ignored** -- it's redundant with the independently present rotation/scale/translation, and only potentially relevant for extrusions
- No mechanism exists to set a specific block color in AutoCAD, so group code 93's purpose is unclear
- **Block attributes differ from INSERT**: The relevant ATTDEF's **handle** (not name) appears in common group code 330, followed by an index (177), a "width" value (44), and the attribute value (302). This four-code sequence repeats for each attribute
- This is important because MULTILEADERs commonly use blocks containing circles and ATTDEFs for labels (e.g., key numbers enclosed in circles)
- Rendered using the BlockExpander from Stage 01

**No content** (content type 0, common group code 172):
- Leader lines only, no annotation content

### MLEADER Color Encoding

Standard entity color uses conventional 62/440 group codes. However, other color values (e.g., common group code 91 for leader line color) use a different encoding: the raw value of the `RGBM` union from the `AcCmEntityColor` class is written directly to DXF as a **signed 32-bit integer**. For example:
- ByBlock = -1056964608 (0xC1000000) instead of the familiar 0
- True color values encode RGB in the lower 24 bits with type flags in the upper 8 bits

### Connection Types

**Horizontal attachment** (left/right):
- Content can attach to the left or right side
- Leader lines approach from the opposite side
- Left attachment: dogleg extends to the right, content is to the right of dogleg end
- Right attachment: dogleg extends to the left, content is to the left of dogleg end

The attachment side is determined by comparing leader line endpoints with the content position: if the leader is to the left of the content, it is a left-side leader (content attaches on the left).

**Vertical attachment** (top/bottom):
- Content can attach at the top or bottom
- No dogleg is drawn
- Leader connects directly to top or bottom of content bbox

### Multiple Leaders and Nodes

An MLEADER entity can have:
- **Zero, one, or two leader nodes** (leaders): Each node represents a separate leader "branch" that points to a different feature but shares the same content
- **Zero or more leader lines per node**: Each line within a node can have different vertices but shares the same dogleg
- Styling applies to all leader lines uniformly; different coloring for individual leader lines is **not possible**

All leaders converge on the same content (text or block).

### Classic LEADER Entity (Legacy)

The classic LEADER entity is legacy (pre-2008) and shares infrastructure with DIMENSION (DIMSTYLE). Key differences from MULTILEADER:
- Content (text/block) is a **separate entity**, not embedded
- Styling uses DIMSTYLE + entity-level overrides
- Uses a "hook line" instead of a dogleg
- The hook line endpoint is **not stored** and must be calculated:
  ```
  if (DIMTAD != 0 && gc73 == 0 && gc41 > 0 && vertexCount >= 2)
  {
      directionVector = (gc211, gc221, gc231) or (1, 0, 0) if absent
      if (gc74 == 1) directionVector = -directionVector  // NB: contradicts DXF spec
      hookEndpoint = lastVertex + (DIMGAP * DIMSCALE + gc41) * directionVector
  }
  ```
- For spline LEADERs (group code 72 == 1): vertices are **fit points** (not control points), degree 3, periodic, not planar or closed
- Autodesk treats LEADER/QLEADER as legacy; default toolbars lack a LEADER button

---

## External Reference Code

### "Demystifying DXF: LEADER and MULTILEADER" (Alan Thomas)
- **URL**: https://atlight.github.io/formats/dxf-leader.html
- **What to study**: The most detailed public documentation of the MLEADER DXF format. Covers the group code sequences, the CONTEXT_DATA structure, leader node/line nesting, and common pitfalls. Includes annotated example DXF snippets and test files.

### GDAL ogrdxf_leader.cpp (MIT/X-style License)
- **URL**: https://github.com/OSGeo/gdal/blob/master/ogr/ogrsf_frmts/dxf/ogrdxf_leader.cpp
- **What to study**: `TranslateMLeader()` function. GDAL implements MLEADER parsing and converts to OGR geometry. Key aspects:
  - How it reads the nested CONTEXT_DATA / LEADER / LEADER_LINE structure
  - `InterpolateSpline()` for smooth leader lines
  - How it handles the dogleg and landing gap
  - Arrow generation at leader tips

### ezdxf MultiLeader Documentation (MIT License)
- **URL**: https://ezdxf.readthedocs.io/en/stable/tutorials/mleader.html
- **What to study**: High-level API for creating MLEADERs. Shows the relationship between MLEADERSTYLE and entity data, content types, and leader construction.

### ezdxf MultiLeader Internals (MIT License)
- **URL**: https://ezdxf.readthedocs.io/en/stable/dxfinternals/entities/mleader.html
- **What to study**: Detailed documentation of the internal DXF structure. Group code sequences, CONTEXT_DATA parsing, and the meaning of each field.

### ezdxf MultiLeaderBuilder (MIT License)
- **URL**: https://github.com/mozman/ezdxf/blob/master/src/ezdxf/entities/mleader.py
- **What to study**: The `virtual_entities()` method that decomposes an MLEADER into renderable geometry. This is the closest reference implementation for what `MLeaderDecomposer.cs` needs to do.

---

## Step-by-Step Implementation Plan

### Step 1: Create MLeaderDecomposer Class

**What**: A class that decomposes an MLEADER entity into render primitives.

**Key structure**:
```csharp
class MLeaderDecomposer
{
    private BlockExpander _blockExpander;
    private TextLayoutEngine _textEngine;
    private PropertyResolver _resolver;
    private RenderLog _log;

    List<RenderNode> Decompose(MultiLeader mleader, Matrix4 parentTransform)
    {
        var nodes = new List<RenderNode>();
        var style = ResolveMLeaderStyle(mleader);

        // 1. Render leader lines + arrowheads
        foreach (var leaderNode in mleader.LeaderNodes)
        {
            nodes.AddRange(RenderLeaderNode(leaderNode, style, parentTransform));
        }

        // 2. Render dogleg(s)
        foreach (var leaderNode in mleader.LeaderNodes)
        {
            nodes.AddRange(RenderDogleg(leaderNode, style, parentTransform));
        }

        // 3. Render content
        nodes.AddRange(RenderContent(mleader, style, parentTransform));

        return nodes;
    }
}
```

**Input**: MultiLeader entity + parent transform.

**Output**: List of render primitives.

**Edge cases**:
- MLEADER with no leader nodes: render content only
- MLEADER with no content (type 0): render leader lines only
- MLEADER with missing style reference: use hardcoded defaults

---

### Step 2: Implement MLeader Style Resolution

**What**: Resolve all style properties, accounting for entity-level overrides.

**Algorithm**:
```csharp
MLeaderProperties ResolveMLeaderStyle(MultiLeader mleader)
{
    var style = mleader.Style; // MLeaderStyle from handle 340
    var overrideFlags = mleader.PropertyOverrideFlags; // group 90

    return new MLeaderProperties
    {
        ContentType = Override(overrideFlags, Flag.ContentType,
            mleader.ContentType, style?.ContentType ?? ContentType.MText),
        LeaderType = Override(overrideFlags, Flag.LeaderType,
            mleader.LeaderType, style?.LeaderType ?? LeaderType.Straight),
        LeaderLineColor = Override(overrideFlags, Flag.LeaderLineColor,
            mleader.LeaderLineColor, style?.LeaderLineColor),
        ArrowheadSize = Override(overrideFlags, Flag.ArrowheadSize,
            mleader.ArrowheadSize, style?.ArrowheadSize ?? 2.5),
        ArrowheadHandle = Override(overrideFlags, Flag.ArrowheadHandle,
            mleader.ArrowheadHandle, style?.ArrowheadHandle),
        TextHeight = Override(overrideFlags, Flag.TextHeight,
            mleader.TextHeight, style?.TextHeight ?? 2.5),
        TextStyle = Override(overrideFlags, Flag.TextStyle,
            mleader.TextStyleHandle, style?.TextStyleHandle),
        DoglegEnabled = Override(overrideFlags, Flag.EnableDogleg,
            mleader.EnableDogleg, style?.EnableDogleg ?? true),
        DoglegLength = Override(overrideFlags, Flag.DoglegLength,
            mleader.DoglegLength, style?.DoglegLength ?? 8.0),
        LandingGap = Override(overrideFlags, Flag.LandingGap,
            mleader.LandingGap, style?.LandingGap ?? 2.0),
        LandingEnabled = Override(overrideFlags, Flag.EnableLanding,
            mleader.EnableLanding, style?.EnableLanding ?? true),
        // ... all other properties
    };
}
```

**Input**: MultiLeader entity with style reference.

**Output**: Fully resolved properties.

**Edge cases**:
- Style handle points to deleted/missing style: use all defaults
- Override flags not set but entity has non-default values: per spec, only use entity values when the corresponding override flag bit is set
- Where the same value appears in both common and context data sections (e.g., text color), AutoCAD uses the **context data section** value
- Color values like common group code 91 use **RGBM encoding** (raw `AcCmEntityColor` union as signed 32-bit int). ByBlock = -1056964608 (0xC1000000), not the familiar 0. ACadSharp may already decode this, but verify

---

### Step 3: Implement Leader Line Rendering

**What**: Render each leader line as a PathNode with appropriate stroke.

**Algorithm**:
```csharp
List<RenderNode> RenderLeaderLines(LeaderNode leaderNode, MLeaderProperties style,
    Matrix4 parentTransform)
{
    var nodes = new List<RenderNode>();

    foreach (var leaderLine in leaderNode.Lines)
    {
        var vertices = leaderLine.Vertices; // List<XYZ>, WCS

        if (vertices.Count < 2) continue;

        PathNode path;

        if (style.LeaderType == LeaderType.Spline)
        {
            // Tessellate spline through vertices
            var splinePoints = TessellateSpline(vertices, precision: 32);
            path = CreatePathFromPoints(splinePoints, parentTransform);
        }
        else // Straight
        {
            path = CreatePathFromPoints(vertices, parentTransform);
        }

        path.Stroke = new StrokeStyle
        {
            Color = style.LeaderLineColor,
            Width = style.LeaderLineWeight,
            DashPattern = ResolveDashPattern(style.LeaderLineType)
        };

        nodes.Add(path);

        // Add arrowhead at first vertex (tip)
        if (vertices.Count >= 2)
        {
            var tipPos = Transform(vertices[0], parentTransform);
            var nextPos = Transform(vertices[1], parentTransform);
            var direction = Normalize(tipPos - nextPos); // points away from leader
            var arrowDirection = -direction; // arrow points toward feature

            nodes.AddRange(CreateArrowhead(tipPos, arrowDirection,
                style.ArrowheadSize, style.ArrowheadHandle, style.LeaderLineColor));
        }
    }

    return nodes;
}
```

**Input**: LeaderNode with its LeaderLines, style, transform.

**Output**: PathNode for each leader line + arrowhead primitives.

**Edge cases**:
- Leader line with only 1 vertex: cannot draw a line, skip
- Spline with only 2 points: degenerate to straight line
- Per-line arrowhead override: some leader lines may have their own arrowhead block/size
- Break points (gaps in leader lines): for each segment between vertex _n_ and _n+1_, check for break point pairs (group codes 11/21/31 and 12/22/32 in LEADER_LINE section). Split the segment at break boundaries and skip rendering the gap portions
- For spline leaders, the arrowhead's back point (final fit point) is not in the DXF data; approximate from the tessellated curve
- Leader lines always terminate at the **dogleg endpoint** (not the landing point), even when the dogleg doesn't draw

---

### Step 4: Implement Spline Tessellation for Smooth Leaders

**What**: Convert spline leader lines into a series of straight segments for rendering.

**Important**: Per the atlight article, MULTILEADER spline vertices are **fit points** (not control points), same as LEADER splines. They define a degree 3 spline that is periodic but not planar or closed, with fit tolerance 0. Start/end tangent directions follow the first/last line segments from straight-mode rendering.

**Algorithm** (Cubic B-spline interpolation through fit points):
```csharp
List<XYZ> TessellateSpline(List<XYZ> fitPoints, int segmentsPerSpan)
{
    if (fitPoints.Count <= 2)
        return fitPoints; // Degenerate: straight line

    // Use Catmull-Rom or cubic B-spline interpolation
    var result = new List<XYZ>();

    for (int i = 0; i < controlPoints.Count - 1; i++)
    {
        var p0 = controlPoints[Math.Max(0, i - 1)];
        var p1 = controlPoints[i];
        var p2 = controlPoints[Math.Min(controlPoints.Count - 1, i + 1)];
        var p3 = controlPoints[Math.Min(controlPoints.Count - 1, i + 2)];

        for (int j = 0; j < segmentsPerSpan; j++)
        {
            double t = (double)j / segmentsPerSpan;
            result.Add(CatmullRomInterpolate(p0, p1, p2, p3, t));
        }
    }
    result.Add(controlPoints.Last());

    return result;
}

XYZ CatmullRomInterpolate(XYZ p0, XYZ p1, XYZ p2, XYZ p3, double t)
{
    double t2 = t * t;
    double t3 = t2 * t;

    double x = 0.5 * ((2 * p1.X) +
        (-p0.X + p2.X) * t +
        (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 +
        (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3);
    // Same for Y and Z

    return new XYZ(x, y, z);
}
```

**Input**: Control point vertices.

**Output**: Densely tessellated point list.

**Edge cases**:
- Very large number of control points: limit total segment count to prevent excessive geometry
- Control points that are collinear: spline degenerates to straight line (correct behavior)

---

### Step 5: Implement Dogleg Rendering

**What**: Draw the horizontal dogleg segment connecting the last leader vertex to the content area.

**Algorithm**:
```csharp
List<RenderNode> RenderDogleg(LeaderNode leaderNode, MLeaderProperties style,
    Matrix4 parentTransform)
{
    if (!style.DoglegEnabled) return empty;

    // Get dogleg start point (last leader line point from CONTEXT_DATA)
    XYZ doglegStart = leaderNode.LastLeaderLinePoint; // group 10/20/30 of leader node
    XYZ doglegDirection = leaderNode.DoglegVector;     // group 11/21/31 of leader node
    double doglegLength = style.DoglegLength;

    if (doglegDirection == XYZ.Zero)
    {
        // Determine direction from content position relative to leader
        doglegDirection = DetermineHorizontalDirection(leaderNode, contentPosition);
    }

    XYZ doglegEnd = doglegStart + Normalize(doglegDirection) * doglegLength;

    // Transform to world
    var startWorld = Transform(doglegStart, parentTransform);
    var endWorld = Transform(doglegEnd, parentTransform);

    var path = new PathNode
    {
        Segments = { MoveTo(startWorld), LineTo(endWorld) },
        Stroke = new StrokeStyle
        {
            Color = style.LeaderLineColor,
            Width = style.LeaderLineWeight,
        }
    };

    return new List<RenderNode> { path };
}
```

**Input**: LeaderNode, style, transform.

**Output**: PathNode for the dogleg segment.

**Edge cases**:
- Dogleg length of 0: no dogleg drawn (but dogleg endpoint still calculated for leader termination)
- Dogleg direction vector is zero: no dogleg drawn
- Spline leaders (common group code 170 == 2): no dogleg drawn
- Vertical attachment: no dogleg (skip)
- Multiple leader nodes: each may have its own dogleg point/direction
- Dogleg breaks: check leader group codes (12/22/32) and (13/23/33) for break point pairs in the dogleg segment

---

### Step 6: Implement MTEXT Content Rendering

**What**: Render the MTEXT annotation content of an MLEADER.

**Algorithm**:
```csharp
List<RenderNode> RenderMTextContent(MultiLeader mleader, MLeaderProperties style,
    Matrix4 parentTransform)
{
    if (style.ContentType != ContentType.MText) return empty;

    var contextData = mleader.ContextData;

    // Get text content from CONTEXT_DATA
    string textContent = contextData.DefaultText; // group 304
    XYZ textPosition = contextData.TextLocation;  // group 12/22/32

    // Build an MTEXT-like structure for the text engine
    double textHeight = style.TextHeight;
    double textWidth = contextData.TextWidth;      // group 44, 0 = no wrap
    int attachment = contextData.TextAttachment;   // 1-9

    // Resolve text style
    string fontName = ResolveFontFromStyleHandle(style.TextStyle);

    // Use TextLayoutEngine from Stage 02
    var textNodes = _textEngine.LayoutMTextContent(
        textContent, textPosition, textHeight, textWidth,
        attachment, fontName, style.TextColor, parentTransform);

    // Landing gap: offset content from dogleg end
    if (style.LandingEnabled && style.LandingGap > 0)
    {
        // Adjust text position by landing gap
        // Direction depends on which side the leader comes from
    }

    return textNodes;
}
```

**Input**: MultiLeader with MTEXT content, style, transform.

**Output**: TextRunNode primitives from the text layout engine.

**Edge cases**:
- Empty text content: nothing to render
- Text content with MTEXT formatting codes: pass through to MTEXT parser
- Text rotation (from CONTEXT_DATA): apply rotation to text group
- Content scale factor: multiply text height and position offsets

---

### Step 7: Implement Block Content Rendering

**What**: Render block content of an MLEADER.

**Algorithm**:
```csharp
List<RenderNode> RenderBlockContent(MultiLeader mleader, MLeaderProperties style,
    Matrix4 parentTransform)
{
    if (style.ContentType != ContentType.Block) return empty;

    var contextData = mleader.ContextData;

    // Get block reference
    ulong blockHandle = contextData.BlockRecordHandle; // group 341
    var blockRecord = mleader.Document.GetObjectByHandle(blockHandle) as BlockRecord;
    if (blockRecord == null)
    {
        _log.Skip(mleader, "block content not found");
        return empty;
    }

    // Block position and transform from CONTEXT_DATA
    XYZ blockPosition = contextData.ContentBasePoint; // or block-specific location
    XYZ blockScale = contextData.BlockScale;           // group 15/25/35
    double blockRotation = contextData.BlockRotation;  // group 46, radians

    // Build INSERT-like transform
    // Follow Stage 00 transform conventions (T * R * S). Angles are radians.
    var blockTransform = Matrix4.CreateTranslation(blockPosition) *
        Matrix4.CreateFromAxisAngle(XYZ.AxisZ, blockRotation) *
        Matrix4.CreateScale(blockScale);

    // Expand block using BlockExpander from Stage 01
    var blockNodes = _blockExpander.ExpandBlock(blockRecord,
        parentTransform * blockTransform);

    // Handle ATTDEF -> ATTRIB substitution
    // MLEADER block attributes differ from INSERT: they reference ATTDEFs by HANDLE
    // (not name). The sequence in common group codes is:
    //   330 = ATTDEF handle, 177 = index, 44 = width, 302 = attribute value
    // This repeats for each attribute.
    foreach (var attDef in blockRecord.Entities.OfType<AttributeDefinition>())
    {
        string overrideValue = GetMLeaderAttributeValue(mleader, attDef.Handle);
        if (overrideValue != null)
        {
            // Replace the ATTDEF-generated text with the override value
            // This requires finding the corresponding TextRunNode and updating it
            ReplaceAttributeText(blockNodes, attDef.Tag, overrideValue);
        }
    }

    return blockNodes;
}
```

**Input**: MultiLeader with block content, style, transform.

**Output**: Block render primitives from BlockExpander.

**Edge cases**:
- Block handle points to non-existent block: skip content
- Block with no ATTDEFs: no substitution needed
- Block scale of (0,0,0): degenerate, skip
- Block rotation is radians in ACadSharp (`DxfReferenceType.IsAngle`); do not assume degrees (DXF stores degrees but the model typically exposes radians)
- Group code 47 matrix (16 values): can be ignored -- redundant with rotation/scale/translation, only potentially relevant for extrusions
- ATTDEF lookup is by **handle** not by name/tag (unlike INSERT ATTRIBs)

---

### Step 8: Implement Connection Type Logic

**What**: Determine which side the leader connects to and adjust content positioning accordingly.

**Algorithm**:
```csharp
ConnectionSide DetermineConnectionSide(LeaderNode leaderNode, XYZ contentPosition)
{
    // Compare the X coordinate of the last leader vertex with the content position
    XYZ lastVertex = GetLastLeaderVertex(leaderNode);

    if (lastVertex.X < contentPosition.X)
    {
        return ConnectionSide.Left; // Leader approaches from the left
    }
    else
    {
        return ConnectionSide.Right; // Leader approaches from the right
    }
}

XYZ AdjustContentForConnection(XYZ basePosition, ConnectionSide side,
    double contentWidth, double landingGap)
{
    switch (side)
    {
        case ConnectionSide.Left:
            // Content is to the right of the dogleg end
            // Text attachment: left side of text box
            return new XYZ(basePosition.X + landingGap, basePosition.Y, 0);

        case ConnectionSide.Right:
            // Content is to the left of the dogleg end
            // Text attachment: right side of text box
            return new XYZ(basePosition.X - landingGap - contentWidth, basePosition.Y, 0);
    }
}
```

**Input**: Leader node, content position.

**Output**: Connection side and adjusted content position.

**Edge cases**:
- Leader directly above/below content (vertical attachment): use vertical connection logic
- Multiple leaders from different sides: use the first leader node to determine attachment side
- Content position coincides with leader endpoint: use dogleg direction to infer side

---

### Step 9: Handle Multiple Leader Nodes

**What**: Process MLEADER entities with multiple leader branches pointing to different features.

**Algorithm**:
```csharp
// In Decompose():
foreach (var leaderNode in mleader.LeaderNodes)
{
    // Each node has its own set of leader lines
    foreach (var leaderLine in leaderNode.Lines)
    {
        // Render leader line with arrowhead
        nodes.AddRange(RenderLeaderLine(leaderLine, style, parentTransform));
    }

    // Each node may have its own dogleg (connecting to the shared content)
    if (style.DoglegEnabled)
    {
        nodes.AddRange(RenderDogleg(leaderNode, style, parentTransform));
    }
}

// Content is shared - render only once
nodes.AddRange(RenderContent(mleader, style, parentTransform));
```

**Input**: MultiLeader with multiple leader nodes.

**Output**: All leader lines + single content.

**Edge cases**:
- Empty leader node (no lines): skip, still render content
- Leader nodes with different line types (one straight, one spline): not typical but handle each node's type independently

---

### Step 10: Integrate into EntityFrontend

**What**: Add the MULTILEADER case to the EntityFrontend dispatcher.

```csharp
case MultiLeader mleader:
    return _mleaderDecomposer.Decompose(mleader, worldTransform);
```

---

## Testing Strategy

### Unit Tests

1. **Style resolution**: Override arrowhead size on entity, verify it takes priority.
2. **Simple straight leader**: One node, one line with two vertices. Verify line + arrowhead + dogleg.
3. **Multi-vertex leader**: Three vertices forming a bent leader. Verify all segments.
4. **Spline leader**: Four vertices, verify smooth tessellation produces reasonable curve.
5. **Dogleg rendering**: Verify horizontal segment from last vertex to content.
6. **Dogleg disabled**: Verify no dogleg segment when disabled.
7. **Landing gap**: Verify spacing between dogleg end and content.
8. **MTEXT content**: Leader with text "Hello World". Verify text is positioned correctly.
9. **Block content**: Leader with block reference. Verify block is expanded at correct position.
10. **Multiple leader nodes**: Two leaders pointing to different locations, shared text. Verify two leader lines, one text.
11. **Connection side detection**: Leader from left -> left attachment. Leader from right -> right attachment.
12. **Arrowhead custom block**: Leader with custom arrow block. Verify block is expanded at tip.
13. **No content type**: Leader lines only (content type 0). Verify lines render without content.

### Integration Tests

14. **Simple MLEADER DXF**: Single leader with MTEXT. Compare with oracle.
15. **MLEADER with block content**: Leader with block. Verify block + leader.
16. **Multiple leaders DXF**: Two leaders converging on one text.
17. **Spline leader DXF**: Smooth curved leader. Compare curve with oracle.
18. **MLEADER in INSERT**: MLEADER inside a block, inserted with scale.

### Test DXF Generation

```python
import ezdxf

doc = ezdxf.new(setup=True)
msp = doc.modelspace()

# Simple MTEXT leader
msp.add_multileader_mtext(
    style="Standard",
    content="Test Leader",
    target_point=(100, 100),
    landing_point=(150, 120),
    insert_point=(200, 120),
)

doc.saveas('test_mleader.dxf')
```

---

## Dependencies

### Depends On
- **Stage 00 (Render Infrastructure)**: PathNode, GroupNode, Transform, PropertyResolver
- **Stage 01 (INSERT/Blocks)**: BlockExpander for block content and custom arrowhead blocks
- **Stage 02 (TEXT/MTEXT)**: TextLayoutEngine for MTEXT content rendering
- **Stage 03 (DIMENSIONS)**: Shares arrowhead rendering logic (can be extracted to shared utility)

### Enables
- No other stages directly depend on MULTILEADER

### External Dependencies
- ACadSharp `MultiLeader`, `MLeaderStyle`, `MLeaderContextData` entity classes
- Spline tessellation (can use existing ACadSharp spline evaluation if available, or implement Catmull-Rom)
