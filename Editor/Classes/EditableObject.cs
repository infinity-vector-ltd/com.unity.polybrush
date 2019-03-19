using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.Polybrush;
using UnityEditor.SettingsManagement;

#if PROBUILDER_4_0_OR_NEWER
using UnityEngine.ProBuilder;
#endif

namespace UnityEditor.Polybrush
{
	[InitializeOnLoad]
	static class HierarchyChanged
	{
		static HierarchyChanged()
		{
			EditorApplication.hierarchyChanged += () =>
			{
				foreach (var gameObject in Selection.gameObjects)
				{
                    var mesh = Util.GetMesh(gameObject);
                    var id = EditableObject.GetMeshId(mesh);

					// if the mesh is an instance managed by polybrush check that it's not a duplicate.
					if (id != -1)
					{
						if (id != gameObject.GetInstanceID() && EditorUtility.InstanceIDToObject(id) != null)
						{
							mesh = PolyMeshUtility.DeepCopy(mesh);
							mesh.name = EditableObject.k_MeshInstancePrefix + gameObject.GetInstanceID();

                            var mf = gameObject.GetComponent<MeshFilter>();
                            var sf = gameObject.GetComponent<SkinnedMeshRenderer>();
                            var polyMesh = gameObject.GetComponent<PolybrushMesh>();

                            if (polyMesh != null)
                            {
                                polyMesh.SetMesh(mesh);
                                PrefabUtility.RecordPrefabInstancePropertyModifications(polyMesh);
                            }
                            else if (mf != null)
                                mf.sharedMesh = mesh;
                            else if (sf != null)
                                sf.sharedMesh = mesh;
						}
					}
				}
			};
		}
	}

    /// <summary>
    /// Stores a cache of the unmodified mesh and meshrenderer
    /// so that the PolyEditor can work non-destructively.  Also
    /// handles ProBuilder compatibility so that brush modes don't
    /// have to deal with it.
    /// </summary>
    internal class EditableObject : IEquatable<EditableObject>, IValid
	{
		const string INSTANCE_MESH_GUID = null;
		internal const string k_MeshInstancePrefix = "PolybrushMesh";

        /// <summary>
        /// Set true to rebuild mesh normals when sculpting the object.
        /// </summary>
        [UserSetting("General Settings", "Rebuild Normals", "After a mesh modification the normals will be recalculated.")]
        internal static Pref<bool> s_RebuildNormals = new Pref<bool>("Mesh.RebuildNormals", true, SettingsScope.Project);

        /// <summary>
        /// Set true to rebuild collider when sculpting the object.
        /// </summary>
        /// <remarks>Only works if gameobject has a MeshCollider component.</remarks>
        [UserSetting("General Settings", "Rebuild MeshCollider", "After a mesh modification the mesh collider will be recalculated.")]
        internal static Pref<bool> s_RebuildCollisions = new Pref<bool>("Mesh.RebuildColliders", true, SettingsScope.Project);

        /// <summary>
        /// Set true to use additional vertex stream when applying brush modification.
        /// Data will be stored in a PolybrushMesh component.
        /// </summary>
        [UserSetting("General Settings", "Use Additional Vertex Streams", "Instead of applying changes directly to the mesh, modifications will be stored in an additionalVertexStreams mesh.  This option can be more performance friendly in some cases.")]
        internal static Pref<bool> s_UseAdditionalVertexStreams = new Pref<bool>("Mesh.UseAdditionalVertexStream", true, SettingsScope.Project);


        private static HashSet<string> UnityPrimitiveMeshNames = new HashSet<string>()
		{
			"Sphere",
			"Capsule",
			"Cylinder",
			"Cube",
			"Plane",
			"Quad"
		};

		// The GameObject being modified.
		internal GameObject gameObjectAttached = null;

		// The mesh that is
		private Mesh _graphicsMesh = null;

		internal Mesh graphicsMesh { get { return _graphicsMesh; } }

		[System.Obsolete("Use graphicsMesh or editMesh instead")]
		internal Mesh mesh { get { return _graphicsMesh; } }

		private PolyMesh _editMesh = null;
        private PolyMesh _skinMeshBaked;
		internal PolyMesh editMesh
        {
            get
            {
                return _editMesh;
            }
        }

        //used only for brush position (raycasting)
        //if it's editing a normal mesh, returns the editMesh (standard behavior).
        //if editing a skinmesh, returns the BakeMesh (a mesh without any skin information but with vertex set to correct positions)
        //it's not intended to edit this mesh, it's used only in read-only (no setter)
        internal PolyMesh visualMesh
        {
            get
            {
                if (_skinMeshRenderer != null)
                {
                    Mesh mesh = new Mesh();
                    _skinMeshRenderer.BakeMesh(mesh);

                    if (_skinMeshBaked == null)
                    {
                        _skinMeshBaked = new PolyMesh(mesh);
                    }
                    else
                    {
                        _skinMeshBaked.SetUnityMesh(mesh);
                    }

                    return _skinMeshBaked;
                }
                else
                {
                    return _editMesh;
                }
            }
        }

        private SkinnedMeshRenderer _skinMeshRenderer;

		// The original mesh.  Can be the same as mesh.
		internal Mesh originalMesh { get; private set; }


        // Where this mesh originated.
        internal ModelSource source { get; private set; }

		// If mesh was an asset or model, save the original GUID
		// internal string sourceGUID { get; private set; }

		// Marks this object as having been modified.
		internal bool modified = false;

		private T GetAttribute<T>(System.Func<Mesh, T> getter) where T : IList
		{
			if(usingVertexStreams) 
			{
				int vertexCount = originalMesh.vertexCount;
				T arr = getter(this.graphicsMesh);
				if(arr != null && arr.Count == vertexCount)
					return arr;
			}
			return getter(originalMesh);
		}

        /// <summary>
        /// Return a mesh that is the combination of both additionalVertexStreams and the originalMesh.
        /// 	- Position
        /// 	- UV0
        /// 	- UV2
        /// 	- UV3
        /// 	- UV4
        /// 	- Color
        /// 	- Tangent
        /// </summary>
        /// <returns>The new PolyMesh object</returns>
        private void GenerateCompositeMesh()
		{
			if(_editMesh == null)
				_editMesh = polybrushMesh.polyMesh;

			_editMesh.Clear();
			_editMesh.name = originalMesh.name;
			_editMesh.vertices	= GetAttribute(x => x.vertices);
			_editMesh.normals	= GetAttribute(x => x.normals);
			_editMesh.colors 	= GetAttribute(x => x.colors32);
			_editMesh.tangents	= GetAttribute(x => x.tangents);
			_editMesh.uv0 = GetAttribute(x => { List<Vector4> l = new List<Vector4>(); x.GetUVs(0, l); return l; } );
			_editMesh.uv1 = GetAttribute(x => { List<Vector4> l = new List<Vector4>(); x.GetUVs(1, l); return l; } );
			_editMesh.uv2 = GetAttribute(x => { List<Vector4> l = new List<Vector4>(); x.GetUVs(2, l); return l; } );
			_editMesh.uv3 = GetAttribute(x => { List<Vector4> l = new List<Vector4>(); x.GetUVs(3, l); return l; } );

            _editMesh.SetSubMeshes(originalMesh);
		}

		internal int vertexCount { get { return originalMesh.vertexCount; } }

		// Convenience getter for gameObject.GetComponent<MeshFilter>().
		internal MeshFilter meshFilter { get; private set; }

		// Convenience getter for gameObject.transform
		internal Transform transform { get { return gameObjectAttached.transform; } }

		// Convenience getter for gameObject.renderer
		internal Renderer renderer { get { return gameObjectAttached.GetComponent<MeshRenderer>(); } }

		// If this object's mesh has been edited, isDirty will be flagged meaning that the mesh should not be
		// cleaned up when finished editing.
		internal bool isDirty = false;

#if PROBUILDER_4_0_OR_NEWER
		// Is the mesh owned by ProBuilder?
		internal bool isProBuilderObject { get; private set; }
#endif
		// Is the mesh using additional vertex streams?
		internal bool usingVertexStreams { get; private set; }

		// Container for polyMesh. @todo remove when Unity fixes
		PolybrushMesh m_PolybrushMesh;

        public PolybrushMesh  polybrushMesh
        {
            get
            {
                if (m_PolybrushMesh == null)
                {
                    Initialize(gameObjectAttached);
                }

                return m_PolybrushMesh;
            }
        }

        // Did this mesh already have an additionalVertexStreams mesh?
        private bool hadVertexStreams = true;

		/// <summary>
		/// Shorthand for checking if object and mesh are non-null.
		/// </summary>
		public bool IsValid
		{
			get
			{
				if(gameObjectAttached == null || graphicsMesh == null)
					return false;

#if PROBUILDER_4_0_OR_NEWER
                if (isProBuilderObject)
                {
                    ProBuilderMesh pbMesh = gameObjectAttached.GetComponent<ProBuilderMesh>();
                    if (pbMesh != null && _editMesh != null && _editMesh.vertexCount != pbMesh.vertexCount)
                    {
                        return false;
                    }
                }
#endif

                return true;
			}
		}

        /// <summary>
        /// Public constructor for editable objects.  Guarantees that a mesh
        /// is editable and takes care of managing the asset.
        /// </summary>
        /// <param name="go">The GameObject used to create the EditableObject</param>
        /// <returns>a new EditableObject if possible, null otherwise</returns>
        internal static EditableObject Create(GameObject go)
		{
			if(go == null)
				return null;

			MeshFilter mf = go.GetComponent<MeshFilter>();
			SkinnedMeshRenderer sf = go.GetComponent<SkinnedMeshRenderer>();

			if(!mf && !sf)
			{
				mf = go.GetComponentsInChildren<MeshFilter>().FirstOrDefault();
				sf = go.GetComponentsInChildren<SkinnedMeshRenderer>().FirstOrDefault();
			}

			if((mf == null || mf.sharedMesh == null) && (sf == null || sf.sharedMesh == null))
				return null;

            return new EditableObject(go);
		}

        private void Initialize(GameObject go)
        {
            gameObjectAttached = go;
#if PROBUILDER_4_0_OR_NEWER
            isProBuilderObject = ProBuilderInterface.IsProBuilderObject(go);
#endif

            Mesh mesh = null;
            MeshRenderer meshRenderer = gameObjectAttached.GetComponent<MeshRenderer>();
            meshFilter = gameObjectAttached.GetComponent<MeshFilter>();
            _skinMeshRenderer = gameObjectAttached.GetComponent<SkinnedMeshRenderer>();
            usingVertexStreams = false;

            originalMesh = go.GetMesh();

            if (originalMesh == null && _skinMeshRenderer != null)
                originalMesh = _skinMeshRenderer.sharedMesh;

            bool configAdvs = s_UseAdditionalVertexStreams;

            m_PolybrushMesh = gameObjectAttached.GetComponent<PolybrushMesh>();

            if (m_PolybrushMesh == null)
            {
                Undo.RecordObject(gameObjectAttached, "Add PolybrushMesh");
                m_PolybrushMesh = gameObjectAttached.AddComponent<PolybrushMesh>();
            }

            //attach the skinmesh ref to the polybrushmesh
            //it will be used when making a prefab containing a skin mesh. The limitation here is that the skin mesh must comes from an asset (which is 99.9999% of the time)
            if (_skinMeshRenderer != null)
            {
                Mesh sharedMesh = _skinMeshRenderer.sharedMesh;
                if (AssetDatabase.Contains(sharedMesh))
                {
                    m_PolybrushMesh.skinMeshRef = sharedMesh;
                }
            }

#if PROBUILDER_4_0_OR_NEWER
            // if it's a probuilder object rebuild the mesh without optimization
            if (isProBuilderObject)
            {
                ProBuilderMesh pb = go.GetComponent<ProBuilderMesh>();

                if (pb != null)
                {
                    pb.ToMesh();
                    pb.Refresh(RefreshMask.All);
                }
            }
#endif

            if (meshRenderer != null || _skinMeshRenderer != null)
            {
                mesh = m_PolybrushMesh.storedMesh;

                if (mesh == null)
                {
                    mesh = PolyMeshUtility.DeepCopy(originalMesh);
                    hadVertexStreams = false;
                }
                else
                {
                    //prevents leak
                    if (!MeshInstanceMatchesGameObject(mesh, gameObjectAttached))
                    {
                        mesh = PolyMeshUtility.DeepCopy(mesh);
                    }
                }

                mesh.name = k_MeshInstancePrefix + gameObjectAttached.GetInstanceID();
                usingVertexStreams = configAdvs;
            }

            polybrushMesh.SetMesh(mesh);
            PrefabUtility.RecordPrefabInstancePropertyModifications(polybrushMesh);
            _graphicsMesh = m_PolybrushMesh.storedMesh;

            source = configAdvs ? ModelSource.AdditionalVertexStreams : PolyEditorUtility.GetMeshGUID(originalMesh);

            GenerateCompositeMesh();
        }

        /// <summary>
        /// Internal constructor.
        /// \sa Create
        /// </summary>
        /// <param name="go"></param>
        private EditableObject(GameObject go)
		{
            Initialize(go);
		}

        ~EditableObject()
		{
            // clean up the composite mesh (if required)
            // delayCall ensures Destroy is called on main thread
            // if(editMesh != null)
            // 	EditorApplication.delayCall += () => { GameObject.DestroyImmediate(editMesh); };
        }

        /// <summary>
        /// Applies mesh changes back to the pb_Object (if necessary).  Optionally does a
        /// mesh rebuild.
        /// </summary>
        /// <param name="rebuildMesh">Only applies to ProBuilder meshes.</param>
        /// <param name="optimize">Determines if the mesh collisions are rebuilt (if that option is enabled) or if
        /// the mehs is a probuilder object, the mesh is optimized (condensed to share verts, other
        /// otpimziations etc) </param>
        internal void Apply(bool rebuildMesh, bool optimize = false)
        {
            if (usingVertexStreams)
            {
                if (s_RebuildNormals.value)
                    PolyMeshUtility.RecalculateNormals(editMesh);

                editMesh.ApplyAttributesToUnityMesh(graphicsMesh);
                graphicsMesh.UploadMeshData(false);
                EditorUtility.SetDirty(gameObjectAttached.GetComponent<Renderer>());

                if(optimize)
                {
                    UpdateMeshCollider();
                }

                if (m_PolybrushMesh.meshFilter)
                    Undo.RecordObject(m_PolybrushMesh.meshFilter, "Assign Polymesh to MeshFilter");

                if (m_PolybrushMesh)
                    m_PolybrushMesh.UpdateMesh();
            }
#if PROBUILDER_4_0_OR_NEWER
            // if it's a probuilder object rebuild the mesh without optimization
            if (isProBuilderObject)
            {
                ProBuilderMesh pbMesh = gameObjectAttached.GetComponent<ProBuilderMesh>();

                // Set the pb_Object.vertices array so that pb_Editor.UpdateSelection
                // can draw the wireframes correctly.
                Vertex[] pbVertices = new Vertex[editMesh.vertices.Length];

                // Prepare position data
                for (int i = 0; i < editMesh.vertices.Length; ++i)
                {
                    Vertex v = new Vertex();
                    v.position = editMesh.vertices[i];

                    if (optimize)
                    {
                        // Prepare tangents data
                        v.tangent = editMesh.tangents[i];
                    }

                    pbVertices[i] = v;
                }

                pbMesh.SetVertices(pbVertices);

                if (optimize)
                {
                    // Set Colors data if they exist
                    if (editMesh.colors != null && editMesh.colors.Length == editMesh.vertexCount)
                    {
                        Color[] colors = System.Array.ConvertAll(editMesh.colors, x => (Color)x);
                        pbMesh.colors = colors;
                    }

                    // Check if UV3/4 have been modified
                    pbMesh.SetUVs(2, editMesh.GetUVs(2));
                    pbMesh.SetUVs(3, editMesh.GetUVs(3));

                    if (rebuildMesh)
                    {
                        pbMesh.ToMesh();
                        pbMesh.Refresh(optimize ? RefreshMask.All : (RefreshMask.Colors | RefreshMask.Normals | RefreshMask.Tangents));
                    }
                }
            }
#endif
            if (usingVertexStreams)
                return;

            if (s_RebuildNormals.value)
                PolyMeshUtility.RecalculateNormals(editMesh);

            editMesh.ApplyAttributesToUnityMesh(graphicsMesh);

            graphicsMesh.RecalculateBounds();

            if(optimize)
            {
                UpdateMeshCollider();
            }

            if (m_PolybrushMesh.meshFilter)
                Undo.RecordObject(m_PolybrushMesh.meshFilter, "Assign Polymesh to MeshFilter");

            m_PolybrushMesh.UpdateMesh();
        }
        /// <summary>
        /// Update the mesh collider
        /// expensive call, delay til optimize is enabled.
        /// </summary>
        private void UpdateMeshCollider()
        {
            if (s_RebuildCollisions.value)
            {
                MeshCollider mc = gameObjectAttached.GetComponent<MeshCollider>();

                if (mc != null)
                {
                    mc.sharedMesh = null;
                    mc.sharedMesh = graphicsMesh;
                }
            }
        }

        /// <summary>
        /// Apply the mesh channel attributes to the graphics mesh.
        /// </summary>
        /// <param name="channel"></param>
        internal void ApplyMeshAttributes(MeshChannel channel = MeshChannel.All)
		{
			editMesh.ApplyAttributesToUnityMesh(_graphicsMesh, channel);

            if (usingVertexStreams)
				_graphicsMesh.UploadMeshData(false);
		}

        /// <summary>
        /// Set the MeshFilter or SkinnedMeshRenderer back to originalMesh.
        /// </summary>
        internal void Revert()
		{
#if PROBUILDER_4_0_OR_NEWER
            if (isProBuilderObject)
                Apply(true, true);
#endif
            RemovePolybrushComponentsIfNecessary();

            if (usingVertexStreams)
			{
				if(!hadVertexStreams)
				{
					GameObject.DestroyImmediate(graphicsMesh);
					MeshRenderer mr = gameObjectAttached.GetComponent<MeshRenderer>();
                    if(mr != null)
                    {
                        mr.additionalVertexStreams = null;
                    }
                }
                return;
			}
            
#if PROBUILDER_4_0_OR_NEWER
            if (isProBuilderObject)
                return;
#endif

			if(	originalMesh == null ||
				(source == ModelSource.Scene && !UnityPrimitiveMeshNames.Contains(originalMesh.name)))
			{
                return;
			}

            if (graphicsMesh != null)
				GameObject.DestroyImmediate(graphicsMesh);

            polybrushMesh.SetMesh(originalMesh);
            PrefabUtility.RecordPrefabInstancePropertyModifications(polybrushMesh);
        }

		public bool Equals(EditableObject rhs)
		{
			return rhs.GetHashCode() == GetHashCode();
		}

		public override bool Equals(object rhs)
		{
			if(rhs == null)
				return gameObjectAttached == null ? true : false;
			else if(gameObjectAttached == null)
				return false;

			if(rhs is EditableObject)
				return rhs.Equals(this);
			else if(rhs is GameObject)
				return ((GameObject)rhs).GetHashCode() == gameObjectAttached.GetHashCode();

			return false;
		}

		public override int GetHashCode()
		{
			return gameObjectAttached != null ? gameObjectAttached.GetHashCode() : base.GetHashCode();
		}

		internal static int GetMeshId(Mesh mesh)
		{
			if (mesh == null)
				return -1;

			int meshId = -1;
			string meshName = mesh.name;

			if(!meshName.StartsWith(k_MeshInstancePrefix) || !int.TryParse(meshName.Replace(k_MeshInstancePrefix, ""), out meshId))
				return meshId;

			return meshId;
		}

		static bool MeshInstanceMatchesGameObject(Mesh mesh, GameObject go)
		{
			int gameObjectId = go.GetInstanceID();
			int meshId = GetMeshId(mesh);

			// If the mesh id doesn't parse to an ID it's definitely not an instance
			if(meshId == -1)
				return false;

			// If the mesh id matches the instance id, it's already a scene instance owned by this object. If doesn't match,
			// next check that the mesh id gameObject does not exist. If it does exist, that means this mesh was duplicated
			// and already belongs to another object in the scene. If it doesn't exist, then it just means that the GameObject
			// id was changed as a normal part of the GameObject lifecycle.
			if (meshId == gameObjectId)
				return true;

			// If it is an instance, and the IDs don't match but no existing GameObject claims this mesh, claim it.
			if (EditorUtility.InstanceIDToObject(meshId) == null)
			{
				mesh.name = k_MeshInstancePrefix + go.GetInstanceID();
				return true;
			}

			// The mesh did not match the gameObject id, and the mesh id points to an already existing object in the scene.
			return false;
		}

        internal void RemovePolybrushComponentsIfNecessary()
        {
            // Check if there's any modification on the PolybrushMesh component.
            // If there is none, remove it from the GameObject.
            if (!m_PolybrushMesh.hasAppliedChanges)
            {
                GameObject.DestroyImmediate(m_PolybrushMesh);
            }
        }
	}
}