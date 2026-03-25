/*
	PolygonOptimizer for 3ds Max 2024

	Replicates three core features from PolygonOptimizerCore:
	1. Single triangle selection via click (pick mode)
	2. View-based occlusion selection (select visible, unoccluded faces)
	3. Octahedron view iteration (loop geodesic camera positions, accumulate selections)

	Works on Editable Poly and Editable Mesh objects.
	Selected faces are stored as the object's face/polygon sub-object selection.
*/

(
-- ============================================================
-- GLOBALS
-- ============================================================
global PolyOpt_PickMode
global PolyOpt_Rollout
global PolyOpt_AccumulatedFaces = #{}  -- bitarray of selected face indices
global PolyOpt_TargetObj = undefined
global PolyOpt_IsIterating = false

-- ============================================================
-- UTILITY FUNCTIONS
-- ============================================================

-- Get face count depending on object type
fn poGetFaceCount obj =
(
	if classOf obj.baseObject == Editable_Poly then
		polyop.getNumFaces obj
	else if classOf obj.baseObject == Editable_Mesh or classOf obj.baseObject == TriMeshGeometry then
		meshop.getNumFaces obj
	else
		0
)

-- Get face normal in world space
fn poGetFaceNormalWorld obj faceIdx =
(
	local n
	if classOf obj.baseObject == Editable_Poly then
		n = polyop.getFaceNormal obj faceIdx
	else
		n = meshop.getFaceNormal obj faceIdx
	-- Transform normal to world space (rotation only)
	n = normalize (n * (inverse obj.transform.rotation))
	-- Correction: multiply by the rotation part of the transform
	n = normalize (n * obj.transform.rotation)
	n
)

-- Get face center in world space
fn poGetFaceCenterWorld obj faceIdx =
(
	local c
	if classOf obj.baseObject == Editable_Poly then
		c = polyop.getFaceCenter obj faceIdx
	else
		c = meshop.getFaceCenter obj faceIdx
	c * obj.transform
)

-- Get face vertices in world space
fn poGetFaceVertsWorld obj faceIdx =
(
	local vertIdxs
	if classOf obj.baseObject == Editable_Poly then
		vertIdxs = polyop.getFaceVerts obj faceIdx
	else
		vertIdxs = meshop.getFace obj faceIdx  -- returns point3 of indices

	local verts = #()
	if classOf obj.baseObject == Editable_Poly then
	(
		for vi in vertIdxs do
			append verts (polyop.getVert obj vi * obj.transform)
	)
	else
	(
		-- meshop.getFace returns a point3 (integer indices)
		append verts ((meshop.getVert obj (vertIdxs.x as integer)) * obj.transform)
		append verts ((meshop.getVert obj (vertIdxs.y as integer)) * obj.transform)
		append verts ((meshop.getVert obj (vertIdxs.z as integer)) * obj.transform)
	)
	verts
)

-- Check if object is a valid target (Editable Poly or Editable Mesh)
fn poIsValidTarget obj =
(
	obj != undefined and not (isDeleted obj) and \
	(classOf obj.baseObject == Editable_Poly or \
	 classOf obj.baseObject == Editable_Mesh or \
	 classOf obj.baseObject == TriMeshGeometry)
)

-- Apply accumulated face selection to the object
fn poApplySelection obj faceBits =
(
	if not (poIsValidTarget obj) then return false
	if classOf obj.baseObject == Editable_Poly then
	(
		subobjectLevel = 4  -- Face sub-object for Editable Poly
		polyop.setFaceSelection obj faceBits
	)
	else
	(
		subobjectLevel = 3  -- Face sub-object for Editable Mesh
		meshop.setFaceSelection obj faceBits
	)
	redrawViews()
	true
)

-- ============================================================
-- OCCLUSION TEST (Ray-based)
-- ============================================================

-- Check if a face is occluded from a given camera position.
-- Fires a ray from camPos toward the face center.
-- If the ray hits something closer than the face, it's occluded.
fn poIsFaceOccluded obj faceIdx camPos thresholdPct =
(
	local faceCenter = poGetFaceCenterWorld obj faceIdx
	local dir = normalize (faceCenter - camPos)
	local distToFace = length (faceCenter - camPos)
	local tolerance = distToFace * (thresholdPct / 100.0)

	local testRay = ray camPos dir

	-- Test against ALL geometry in the scene (including the object itself)
	local hitResult = intersectRay obj testRay

	if hitResult != undefined then
	(
		local hitDist = length (hitResult.pos - camPos)
		-- If hit is significantly closer than the face center, face is occluded
		if hitDist < (distToFace - tolerance) then
			return true
	)

	-- Also test against other geometry in the scene
	for otherObj in geometry where otherObj != obj and otherObj.isHidden == false do
	(
		local otherHit = intersectRay otherObj testRay
		if otherHit != undefined then
		(
			local otherDist = length (otherHit.pos - camPos)
			if otherDist < (distToFace - tolerance) then
				return true
		)
	)

	false
)

-- ============================================================
-- 1. SINGLE FACE PICK MODE
-- ============================================================

tool PolyOpt_PickMode
(
	local lastObj = undefined

	on mousePoint clickno do
	(
		if clickno == 1 then
		(
			local vp = viewport.activeViewport
			local vpRay = mapScreenToWorldRay mouse.pos

			-- Find the closest hit across all geometry
			local bestObj = undefined
			local bestFace = 0
			local bestDist = 1e30

			-- First check the target object
			if poIsValidTarget PolyOpt_TargetObj then
			(
				local hitResult = intersectRay PolyOpt_TargetObj vpRay
				if hitResult != undefined then
				(
					local d = length (hitResult.pos - vpRay.pos)
					if d < bestDist then
					(
						bestDist = d
						bestObj = PolyOpt_TargetObj
						bestFace = hitResult.faceIndex
					)
				)
			)

			-- If we have no target, check all editable geometry
			if bestObj == undefined then
			(
				for obj in geometry where obj.isHidden == false and (poIsValidTarget obj) do
				(
					local hitResult = intersectRay obj vpRay
					if hitResult != undefined then
					(
						local d = length (hitResult.pos - vpRay.pos)
						if d < bestDist then
						(
							bestDist = d
							bestObj = obj
							bestFace = hitResult.faceIndex
						)
					)
				)
			)

			if bestObj != undefined and bestFace > 0 then
			(
				-- If target changed, reset selection
				if PolyOpt_TargetObj != bestObj then
				(
					PolyOpt_TargetObj = bestObj
					PolyOpt_AccumulatedFaces = #{}
					if PolyOpt_Rollout != undefined then
						PolyOpt_Rollout.lblTarget.text = bestObj.name
				)

				-- Backface check
				local doSelect = true
				if PolyOpt_Rollout != undefined and PolyOpt_Rollout.chkIgnoreBackface.checked then
				(
					local fn_ = poGetFaceNormalWorld bestObj bestFace
					local fc = poGetFaceCenterWorld bestObj bestFace
					local viewDir = normalize (fc - vpRay.pos)
					if (dot fn_ viewDir) > 0 then
						doSelect = false
				)

				if doSelect then
				(
					-- Toggle: Shift deselects, otherwise toggle
					if keyboard.shiftPressed then
					(
						PolyOpt_AccumulatedFaces[bestFace] = false
					)
					else
					(
						PolyOpt_AccumulatedFaces[bestFace] = not PolyOpt_AccumulatedFaces[bestFace]
					)

					poApplySelection bestObj PolyOpt_AccumulatedFaces
					if PolyOpt_Rollout != undefined then
						PolyOpt_Rollout.lblSelCount.text = (PolyOpt_AccumulatedFaces.numberSet as string) + " faces"
				)
			)
		)
	)

	on mouseMove clickno do () -- required stub
	on mouseAbort clickno do ()
)


-- ============================================================
-- 2. VIEW-BASED OCCLUSION SELECTION
-- ============================================================

fn poSelectCurrentView obj thresholdPct ignoreBackface: true =
(
	if not (poIsValidTarget obj) then return #{}

	local faceCount = poGetFaceCount obj
	if faceCount == 0 then return #{}

	-- Get camera position from active viewport
	local camTM = viewport.getTM()
	local camPos = camTM.position  -- This is actually wrong for getTM
	-- viewport.getTM() returns the inverse camera matrix
	-- Camera position = inverse of getTM translation
	local invTM = inverse (viewport.getTM())
	camPos = invTM.position

	local camDir = -invTM.row3  -- Camera forward direction (negative Z)

	local newSelection = #{}

	-- Get viewport dimensions for screen-space checks
	local vpSize = getViewSize()
	local vpWidth = vpSize.x
	local vpHeight = vpSize.y

	for f = 1 to faceCount do
	(
		-- Skip already selected faces
		if PolyOpt_AccumulatedFaces[f] then continue

		local faceCenter = poGetFaceCenterWorld obj f
		local faceNormal = poGetFaceNormalWorld obj f

		-- Backface culling
		if ignoreBackface then
		(
			local viewDir = normalize (faceCenter - camPos)
			if (dot faceNormal viewDir) > 0 then continue
		)

		-- Check if face center is within viewport (project to screen)
		local screenPos = gw.wPoint faceCenter  -- Projects world point to screen
		if screenPos.x < 0 or screenPos.x >= vpWidth or screenPos.y < 0 or screenPos.y >= vpHeight then
			continue

		-- Occlusion test: fire ray from camera toward face center
		if not (poIsFaceOccluded obj f camPos thresholdPct) then
		(
			newSelection[f] = true
		)
	)

	newSelection
)


-- ============================================================
-- 3. OCTAHEDRON GENERATION
-- ============================================================

fn poBuildOctahedronVertices resolution =
(
	-- Initial upper-hemisphere octahedron (5 vertices, 4 triangles)
	local verts = #([0, 0, 1], [1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -1, 0])
	-- Note: Max uses Z-up, so top vertex is [0,0,1], equator is XY plane
	local tris = #(#(1, 4, 2), #(1, 2, 5), #(1, 5, 3), #(1, 3, 4))

	local subdivisions = amax 0 (resolution - 1)

	for s = 1 to subdivisions do
	(
		local edgeMids = #()  -- array of #(minIdx, maxIdx, midpointIdx)
		local newTris = #()

		-- Helper: get or create midpoint vertex
		fn getMidpoint verts edgeMids a b =
		(
			local lo = amin a b
			local hi = amax a b
			-- Search existing midpoints
			for em in edgeMids do
			(
				if em[1] == lo and em[2] == hi then return em[3]
			)
			-- Create new midpoint
			local midVert = normalize ((verts[a] + verts[b]) / 2.0)
			append verts midVert
			local midIdx = verts.count
			append edgeMids #(lo, hi, midIdx)
			midIdx
		)

		for tri in tris do
		(
			local i0 = tri[1]
			local i1 = tri[2]
			local i2 = tri[3]

			local m01 = getMidpoint verts edgeMids i0 i1
			local m12 = getMidpoint verts edgeMids i1 i2
			local m02 = getMidpoint verts edgeMids i0 i2

			append newTris #(i0, m01, m02)
			append newTris #(m01, i1, m12)
			append newTris #(m02, m12, i2)
			append newTris #(m01, m12, m02)
		)

		tris = newTris
	)

	-- Return only unique vertices (just the vertex positions on unit sphere)
	-- Remove duplicates
	local uniqueVerts = #()
	for v in verts do
	(
		local found = false
		for uv in uniqueVerts while not found do
		(
			if length (v - uv) < 0.0001 then found = true
		)
		if not found then append uniqueVerts v
	)

	uniqueVerts
)


-- ============================================================
-- ROLLOUT UI
-- ============================================================

try (destroyDialog PolyOpt_Rollout) catch()

rollout PolyOpt_Rollout "Polygon Optimizer" width:220 height:520
(
	-- Target
	group "Target"
	(
		label lblTargetLabel "Object:" across:2
		label lblTarget "<none>" align:#right
		button btnPickTarget "Pick Target Object" width:190
	)

	-- Selection Info
	group "Selection"
	(
		label lblSelLabel "Selected:" across:2
		label lblSelCount "0 faces" align:#right
		button btnClearSel "Clear Selection" width:190
	)

	-- Single Selection Mode
	group "1. Single Face Pick"
	(
		checkbox chkIgnoreBackface "Ignore Backfacing" checked:true
		button btnStartPick "Start Pick Mode" width:190 height:28
		button btnStopPick "Stop Pick Mode" width:190 height:22 enabled:false
		label lblPickHelp "Click: toggle  |  Shift+Click: deselect" align:#left
	)

	-- View-Based Selection
	group "2. View Selection"
	(
		spinner spnOccThreshold "Occlusion Threshold %:" range:[0.1, 50.0, 2.0] type:#float fieldwidth:50
		button btnSelectView "Select Current View" width:190 height:28
	)

	-- Octahedron Iteration
	group "3. Iterate Views"
	(
		spinner spnResolution "Octahedron Resolution:" range:[1, 8, 4] type:#integer fieldwidth:50
		spinner spnCamWidth "Camera Width Scale:" range:[0.5, 10.0, 2.0] type:#float fieldwidth:50
		spinner spnRenderWait "Wait per View (ms):" range:[10, 1000, 50] type:#integer fieldwidth:50
		label lblVertCount "Camera positions: 17"
		button btnIterateViews "Iterate All Views" width:190 height:28
		button btnStopIterate "Stop" width:190 height:22 enabled:false
		progressBar pbProgress color:green value:0
	)

	-- Update vertex count preview when resolution changes
	on spnResolution changed val do
	(
		local testVerts = poBuildOctahedronVertices val
		lblVertCount.text = "Camera positions: " + (testVerts.count as string)
	)

	on btnPickTarget pressed do
	(
		local obj = pickObject message:"Click on target object" forceListenerFocus:false
		if obj != undefined then
		(
			if poIsValidTarget obj then
			(
				PolyOpt_TargetObj = obj
				lblTarget.text = obj.name
				PolyOpt_AccumulatedFaces = #{}
				lblSelCount.text = "0 faces"
				select obj
			)
			else
			(
				messageBox "Object must be Editable Poly or Editable Mesh.\nConvert it first." title:"Invalid Object"
			)
		)
	)

	on btnClearSel pressed do
	(
		PolyOpt_AccumulatedFaces = #{}
		lblSelCount.text = "0 faces"
		if poIsValidTarget PolyOpt_TargetObj then
			poApplySelection PolyOpt_TargetObj PolyOpt_AccumulatedFaces
	)

	-- Single Pick
	on btnStartPick pressed do
	(
		if not (poIsValidTarget PolyOpt_TargetObj) then
		(
			messageBox "Pick a target object first." title:"No Target"
			return()
		)
		startTool PolyOpt_PickMode
		btnStartPick.enabled = false
		btnStopPick.enabled = true
	)

	on btnStopPick pressed do
	(
		stopTool PolyOpt_PickMode
		btnStartPick.enabled = true
		btnStopPick.enabled = false
	)

	-- View Selection
	on btnSelectView pressed do
	(
		if not (poIsValidTarget PolyOpt_TargetObj) then
		(
			messageBox "Pick a target object first." title:"No Target"
			return()
		)

		local newFaces = poSelectCurrentView PolyOpt_TargetObj spnOccThreshold.value \
			ignoreBackface:chkIgnoreBackface.checked

		-- Accumulate
		PolyOpt_AccumulatedFaces = PolyOpt_AccumulatedFaces + newFaces
		poApplySelection PolyOpt_TargetObj PolyOpt_AccumulatedFaces
		lblSelCount.text = (PolyOpt_AccumulatedFaces.numberSet as string) + " faces"
	)

	-- Iterate Views
	on btnIterateViews pressed do
	(
		if not (poIsValidTarget PolyOpt_TargetObj) then
		(
			messageBox "Pick a target object first." title:"No Target"
			return()
		)

		local obj = PolyOpt_TargetObj
		local resolution = spnResolution.value
		local camWidthScale = spnCamWidth.value
		local occThreshold = spnOccThreshold.value
		local waitMs = spnRenderWait.value
		local ignoreBack = chkIgnoreBackface.checked

		-- Build octahedron camera positions
		local unitVerts = poBuildOctahedronVertices resolution
		local vertCount = unitVerts.count

		-- Compute sphere radius and center from object bounding box
		local bb = nodeGetBoundingBox obj obj.transform
		local bbMin = bb[1]
		local bbMax = bb[2]
		local halfExtents = (bbMax - bbMin) / 2.0
		local r = halfExtents.x + halfExtents.y + halfExtents.z
		-- Center at bottom of bounding box (XY center, Z min) -- Z-up in Max
		local center = [(bbMin.x + bbMax.x) / 2.0, (bbMin.y + bbMax.y) / 2.0, bbMin.z]

		-- Scale vertices to world positions
		local worldVerts = for v in unitVerts collect (center + v * r)

		-- Calculate orthographic camera width
		local maxDim = amax halfExtents.x (amax halfExtents.y halfExtents.z)
		local orthoWidth = maxDim * 2.0 * camWidthScale

		-- Save current viewport state
		local savedTM = viewport.getTM()
		local savedFOV = viewport.GetFOV()

		-- Switch to orthographic
		viewport.setType #view_persp_user  -- Ensure it's a user view we can control

		PolyOpt_IsIterating = true
		btnIterateViews.enabled = false
		btnStopIterate.enabled = true
		pbProgress.value = 0

		local lookTarget = center

		for i = 1 to vertCount while PolyOpt_IsIterating do
		(
			local camPos = worldVerts[i]
			local lookDir = normalize (lookTarget - camPos)

			-- Compute stable up vector (avoid parallel to look direction)
			local up = [0, 0, 1]  -- Z-up default
			if (abs (dot lookDir [0, 0, 1])) > 0.99 then
				up = [0, 1, 0]  -- Use Y-up when looking along Z

			-- Build camera transform matrix
			-- Camera in Max: -Z is forward, X is right, Y is up
			local zAxis = -lookDir  -- Camera looks along -Z
			local xAxis = normalize (cross up zAxis)
			local yAxis = normalize (cross zAxis xAxis)

			local camTM = matrix3 xAxis yAxis zAxis camPos
			viewport.setTM camTM

			-- Set orthographic field of view
			-- In Max, FOV for ortho view controls the visible width
			viewport.setFOV (orthoWidth)

			-- Wait for render
			forceCompleteRedraw()
			sleep (waitMs / 1000.0)

			-- Select visible faces from this view
			local newFaces = poSelectCurrentView obj occThreshold ignoreBackface:ignoreBack
			PolyOpt_AccumulatedFaces = PolyOpt_AccumulatedFaces + newFaces

			-- Update UI
			pbProgress.value = (i as float / vertCount * 100.0)
			lblSelCount.text = (PolyOpt_AccumulatedFaces.numberSet as string) + " faces"

			windows.processPostedMessages()  -- Keep UI responsive
		)

		-- Apply final selection
		poApplySelection obj PolyOpt_AccumulatedFaces

		-- Restore viewport
		viewport.setTM savedTM
		viewport.setFOV savedFOV
		forceCompleteRedraw()

		PolyOpt_IsIterating = false
		btnIterateViews.enabled = true
		btnStopIterate.enabled = false
		pbProgress.value = 100

		messageBox ("View iteration complete.\n" + \
			(PolyOpt_AccumulatedFaces.numberSet as string) + " faces selected from " + \
			(vertCount as string) + " camera positions.") title:"Done"
	)

	on btnStopIterate pressed do
	(
		PolyOpt_IsIterating = false
	)

	-- Cleanup on close
	on PolyOpt_Rollout close do
	(
		PolyOpt_IsIterating = false
		try (stopTool PolyOpt_PickMode) catch()
	)

	-- Initialize vertex count label
	on PolyOpt_Rollout open do
	(
		local testVerts = poBuildOctahedronVertices spnResolution.value
		lblVertCount.text = "Camera positions: " + (testVerts.count as string)

		-- If there's a current selection, use it as target
		if selection.count == 1 and (poIsValidTarget selection[1]) then
		(
			PolyOpt_TargetObj = selection[1]
			lblTarget.text = PolyOpt_TargetObj.name
		)
	)
)

createDialog PolyOpt_Rollout style:#(#style_titlebar, #style_sysmenu, #style_toolwindow, #style_resizing)

)  -- end script scope
