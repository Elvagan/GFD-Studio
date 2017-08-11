﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AtlusGfdEditor.GUI.Controls.ModelView;
using AtlusGfdLib;
using CSharpImageLibrary;
using CSharpImageLibrary.Headers;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;
using OpenTK.Input;

namespace AtlusGfdEditor.GUI.Controls
{
    public partial class ModelViewControl : GLControl
    {
        private Model mModel;
        private GLShaderProgram mShaderProgram;
        private List<GLGeometry> mGeometries = new List<GLGeometry>();
        private GLPerspectiveCamera mCamera;
        private bool mIsModelLoaded;
        private bool mIsFieldModel;
        private Archive mFieldTextures;
        private int mLastMouseX;
        private int mLastMouseY;

        public ModelViewControl() : base( 
            new GraphicsMode(32, 24, 0, 4),
            3,
            3,
            GraphicsContextFlags.Debug | GraphicsContextFlags.ForwardCompatible)
        {
            InitializeComponent();
            Dock = DockStyle.Fill;

            // required to use GL in the context of this control
            MakeCurrent();
        }

        /// <summary>
        /// Load a model for displaying in the control.
        /// </summary>
        /// <param name="model"></param>
        public void LoadModel( Model model )
        {
            mModel = model;
            DeleteModel();
            LoadModel();
        }

        /// <summary> 
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose( bool disposing )
        {
            if ( disposing && ( components != null ) )
            {
                components.Dispose();

                if ( mShaderProgram != null )
                    mShaderProgram.Dispose();

                DeleteModel();
            }

            base.Dispose( disposing );
        }

        /// <summary>
        /// Executed during the initial load of the control.
        /// </summary>
        /// <param name="e"></param>
        protected override void OnLoad( EventArgs e )
        {
            LogGLInfo();
            InitializeGL();
            InitializeGLShaders();
        }

        /// <summary>
        /// Executed when a frame is rendered.
        /// </summary>
        /// <param name="e"></param>
        protected override void OnPaint( PaintEventArgs e )
        {
            // clear the buffers
            GL.Clear( ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit );

            if ( mIsModelLoaded )
            {
                foreach ( var geometry in mGeometries )
                {
                    // set up model view projection matrix uniforms
                    var modelViewProj = geometry.ModelMatrix * mCamera.CalculateViewProjectionMatrix();

                    mShaderProgram.SetUniform( "modelViewProj", modelViewProj );

                    // set material uniforms
                    mShaderProgram.SetUniform( "hasDiffuse", geometry.Material.HasDiffuse );

                    if ( geometry.Material.HasDiffuse )
                    {
                        mShaderProgram.SetUniform( "hasDiffuse", true );
                        GL.BindTexture( TextureTarget.Texture2D, geometry.Material.DiffuseTextureId );
                    }

                    mShaderProgram.SetUniform( "matAmbient", geometry.Material.Ambient );
                    mShaderProgram.SetUniform( "matDiffuse", geometry.Material.Diffuse );
                    mShaderProgram.SetUniform( "matSpecular", geometry.Material.Specular );
                    mShaderProgram.SetUniform( "matEmissive", geometry.Material.Emissive );

                    mShaderProgram.Use();

                    // use the vertex array
                    GL.BindVertexArray( geometry.VertexArrayId );

                    // draw the polygon
                    GL.DrawElements( PrimitiveType.Triangles, geometry.ElementIndexCount, DrawElementsType.UnsignedInt, 0 );
                }
            }

            SwapBuffers();
        }

        /// <summary>
        /// Executed when control is resized.
        /// </summary>
        /// <param name="e"></param>
        protected override void OnResize( EventArgs e )
        {
            if ( !mIsModelLoaded )
                return;

            mCamera.AspectRatio = ( float )Width / ( float )Height;
            GL.Viewport( ClientRectangle );
        }

        /// <summary>
        /// Log GL info for diagnostics.
        /// </summary>
        private void LogGLInfo()
        {
            // todo: log to file? would help with debugging crashes on clients
            Trace.WriteLine( "NOTICE: GL Info" );
            Trace.WriteLine( $"     Vendor         {GL.GetString( StringName.Vendor )}" );
            Trace.WriteLine( $"     Renderer       {GL.GetString( StringName.Renderer )}" );
            Trace.WriteLine( $"     Version        {GL.GetString( StringName.Version )}" );
            Trace.WriteLine( $"     Extensions     {GL.GetString( StringName.Extensions )}" );
            Trace.WriteLine( $"     GLSL version   {GL.GetString( StringName.ShadingLanguageVersion )}" );
            Trace.WriteLine( "" );
        }

        /// <summary>
        /// Initializes GL state before rendering starts.
        /// </summary>
        private void InitializeGL()
        {
            GL.ClearColor( Color.LightGray );
            GL.FrontFace( FrontFaceDirection.Ccw );
            GL.CullFace( CullFaceMode.Back );
            GL.Enable( EnableCap.CullFace );
            GL.Enable( EnableCap.DepthTest );
        }

        /// <summary>
        /// Initializes shaders and links the shader program. Assumes only 1 shader program will be used.
        /// </summary>
        private void InitializeGLShaders()
        {
            if ( !GLShaderProgram.TryCreate(
                Path.Combine( AppDomain.CurrentDomain.BaseDirectory, "VertexShader.glsl" ),
                Path.Combine( AppDomain.CurrentDomain.BaseDirectory, "FragmentShader.glsl" ),
                out mShaderProgram ) )
            {
                Trace.WriteLine( "WARNING: Failed to compile shaders. Trying to use basic shaders.." );

                if ( !GLShaderProgram.TryCreate(
                    Path.Combine( AppDomain.CurrentDomain.BaseDirectory, "VertexShaderBasic.glsl" ),
                    Path.Combine( AppDomain.CurrentDomain.BaseDirectory, "FragmentShaderBasic.glsl" ),
                    out mShaderProgram ) )
                {
                    Trace.WriteLine( "ERROR: Failed to compile basic shaders." );
                    throw new Exception( "Failed to compile basic shaders." );
                }
            }

            // register shader uniforms
            mShaderProgram.RegisterUniform<Matrix4>( "modelViewProj" );
            mShaderProgram.RegisterUniform<bool>( "hasDiffuse" );
            mShaderProgram.RegisterUniform<Vector4>( "matAmbient" );
            mShaderProgram.RegisterUniform<Vector4>( "matDiffuse" );
            mShaderProgram.RegisterUniform<Vector4>( "matSpecular" );
            mShaderProgram.RegisterUniform<Vector4>( "matEmissive" );
        }

        //
        // Loading / saving model
        //

        private void LoadModel()
        {
            if ( mModel.Scene == null )
                return;

            InitializeCamera();

            foreach ( var node in mModel.Scene.Nodes )
            {
                if ( !node.HasAttachments )
                    continue;

                foreach ( var attachment in node.Attachments )
                {
                    if ( attachment.Type != NodeAttachmentType.Geometry )
                        continue;

                    var geometry = CreateGLGeometry( attachment.GetValue<Geometry>() );
                    geometry.ModelMatrix = ToMatrix4( node.WorldTransform );

                    mGeometries.Add( geometry );
                }
            }

            mIsModelLoaded = true;

            Invalidate();
        }

        private void DeleteModel()
        {
            if ( !mIsModelLoaded )
                return;

            mIsModelLoaded = false;

            GL.BindVertexArray( 0 );
            GL.BindBuffer( BufferTarget.ArrayBuffer, 0 );
            GL.BindBuffer( BufferTarget.ElementArrayBuffer, 0 );
            GL.BindTexture( TextureTarget.Texture2D, 0 );

            foreach ( var geometry in mGeometries )
            {
                GL.DeleteVertexArray( geometry.VertexArrayId );
                GL.DeleteBuffer( geometry.PositionBufferId );
                GL.DeleteBuffer( geometry.NormalBufferId );
                GL.DeleteBuffer( geometry.TextureCoordinateChannel0BufferId );
                GL.DeleteBuffer( geometry.ElementBufferId );

                if ( geometry.Material.HasDiffuse )
                {
                    GL.DeleteTexture( geometry.Material.DiffuseTextureId );
                }
            }

            mGeometries.Clear();
        }

        private void InitializeCamera()
        {
            var cameraFov = 45f;

            BoundingSphere bSphere;
            if ( !mModel.Scene.BoundingSphere.HasValue )
            {
                if ( mModel.Scene.BoundingBox.HasValue )
                {
                    bSphere = BoundingSphere.Calculate( mModel.Scene.BoundingBox.Value );
                }
                else
                {
                    bSphere = new BoundingSphere( new System.Numerics.Vector3(), 0 );
                }
            }
            else
            {
                bSphere = mModel.Scene.BoundingSphere.Value;
            }

            var cameraFovInRad = MathHelper.DegreesToRadians( cameraFov );
            var distance = ( float )( ( bSphere.Radius * 2.0f ) / Math.Tan( cameraFovInRad / 2.0f ) );
            var cameraTranslation = new Vector3( bSphere.Center.X, bSphere.Center.Y, bSphere.Center.Z ) + new Vector3( 0, -50, 300 );

            mCamera = new GLPerspectiveFreeCamera( cameraTranslation, 1f, 100000f, cameraFov, ( float )Width / ( float )Height, Quaternion.Identity );
        }

        private static Matrix4 ToMatrix4( System.Numerics.Matrix4x4 matrix )
        {
            return new Matrix4(
                matrix.M11, matrix.M12, matrix.M13, matrix.M14,
                matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                matrix.M31, matrix.M32, matrix.M33, matrix.M34,
                matrix.M41, matrix.M42, matrix.M43, matrix.M44 );
        }

        //
        // Texture stuff
        //

        private static int CreateGLTexture( Texture texture )
        {
            int textureId = GL.GenTexture();
            GL.BindTexture( TextureTarget.Texture2D, textureId );

            var ddsHeader = new DDS_Header( new MemoryStream( texture.Data ) );

            // todo: identify and retrieve values from texture
            // todo: disable mipmaps for now, they often break and show up as black ( eg after replacing a texture )
            SetGLTextureParameters( TextureWrapMode.Repeat, TextureWrapMode.Repeat, TextureMagFilter.Linear, TextureMinFilter.Linear, ddsHeader.dwMipMapCount - 1 );

            var format = GetGLTexturePixelInternalFormat( ddsHeader.Format );

            SetGLTextureDDSImageData( ddsHeader.Width, ddsHeader.Height, format, ddsHeader.dwMipMapCount, texture.Data, 0x80 );

            return textureId;
        }

        private static int CreateGLTexture( FieldTexture texture )
        {
            int textureId = GL.GenTexture();
            GL.BindTexture( TextureTarget.Texture2D, textureId );

            // todo: identify and retrieve values from texture
            // todo: disable mipmaps for now, they often break and show up as black ( eg after replacing a texture )
            SetGLTextureParameters( TextureWrapMode.Repeat, TextureWrapMode.Repeat, TextureMagFilter.Linear, TextureMinFilter.Linear, texture.MipMapCount - 1 );

            var format = GetGLTexturePixelInternalFormat( texture.Flags );

            SetGLTextureDDSImageData( texture.Width, texture.Height, format, texture.MipMapCount, texture.Data );

            return textureId;
        }

        private static PixelInternalFormat GetGLTexturePixelInternalFormat( ImageEngineFormat format )
        {
            switch ( format )
            {
                case ImageEngineFormat.DDS_DXT1:
                    return PixelInternalFormat.CompressedRgbaS3tcDxt1Ext;

                case ImageEngineFormat.DDS_DXT3:
                    return PixelInternalFormat.CompressedRgbaS3tcDxt3Ext;

                case ImageEngineFormat.DDS_DXT5:
                    return PixelInternalFormat.CompressedRgbaS3tcDxt5Ext;

                default:
                    throw new NotImplementedException(format.ToString());
            }
        }

        private static PixelInternalFormat GetGLTexturePixelInternalFormat( FieldTextureFlags flags )
        {
            PixelInternalFormat format = PixelInternalFormat.CompressedRgbaS3tcDxt1Ext;
            if ( flags.HasFlag( FieldTextureFlags.DXT3 ) )
            {
                format = PixelInternalFormat.CompressedRgbaS3tcDxt3Ext;
            }
            else if ( flags.HasFlag( FieldTextureFlags.DXT5 ) )
            {
                format = PixelInternalFormat.CompressedRgbaS3tcDxt5Ext;
            }

            return format;
        }

        private static void SetGLTextureParameters( TextureWrapMode wrapS, TextureWrapMode wrapT, TextureMagFilter magFilter, TextureMinFilter minFilter, int maxMipLevel )
        {
            GL.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapS, ( int )wrapS );
            GL.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapT, ( int )wrapT );
            GL.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, ( int )magFilter );
            GL.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, ( int )minFilter );
            GL.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, maxMipLevel );
        }

        private static void SetGLTextureDDSImageData( int width, int height, PixelInternalFormat format, int mipMapCount, byte[] data, int dataOffset = 0 )
        {
            var dataHandle = GCHandle.Alloc( data, GCHandleType.Pinned );

            SetGLTextureDDSImageData( width, height, format, mipMapCount, ( dataHandle.AddrOfPinnedObject() + dataOffset ) );

            dataHandle.Free();
        }

        private static void SetGLTextureDDSImageData( int width, int height, PixelInternalFormat format, int mipMapCount, IntPtr data )
        {
            int mipWidth = width;
            int mipHeight = height;
            int blockSize = ( format == PixelInternalFormat.CompressedRgbaS3tcDxt1Ext ) ? 8 : 16;
            int mipOffset = 0;

            for ( int mipLevel = 0; mipLevel < mipMapCount; mipLevel++ )
            {
                int mipSize = ( ( mipWidth * mipHeight ) / 16 ) * blockSize;

                GL.CompressedTexImage2D( TextureTarget.Texture2D, mipLevel, format, mipWidth, mipHeight, 0, mipSize, data + mipOffset );

                mipOffset += mipSize;
                mipWidth /= 2;
                mipHeight /= 2;
            }
        }

        //
        // Model stuff
        //

        private GLGeometry CreateGLGeometry( Geometry geometry )
        {
            var glGeometry = new GLGeometry();

            // vertex array
            glGeometry.VertexArrayId = GL.GenVertexArray();
            GL.BindVertexArray( glGeometry.VertexArrayId );

            // positions
            glGeometry.PositionBufferId = CreateGLVertexAttributeBuffer( geometry.Vertices.Length * Vector3.SizeInBytes, geometry.Vertices, 0, 3 );

            // normals
            glGeometry.NormalBufferId = CreateGLVertexAttributeBuffer( geometry.Normals.Length * Vector3.SizeInBytes, geometry.Normals, 1, 3 );

            if ( geometry.TexCoordsChannel0 != null )
            {
                // texture coordinate channel 0
                glGeometry.TextureCoordinateChannel0BufferId = CreateGLVertexAttributeBuffer( geometry.TexCoordsChannel0.Length * Vector2.SizeInBytes, geometry.TexCoordsChannel0, 2, 2 );
            }

            // element index buffer
            glGeometry.ElementBufferId = CreateGLBuffer( BufferTarget.ElementArrayBuffer, geometry.Triangles.Length * Triangle.SizeInBytes, geometry.Triangles );
            glGeometry.ElementIndexCount = geometry.Triangles.Length * 3;

            // material
            glGeometry.Material = CreateGLMaterial( mModel.MaterialDictionary[geometry.MaterialName] );

            return glGeometry;
        }

        private static int CreateGLBuffer<T>( BufferTarget target, int size, T[] data ) where T : struct
        {
            // generate buffer id
            int buffer = GL.GenBuffer();

            // mark buffer as active
            GL.BindBuffer( target, buffer );

            // upload data to buffer store
            GL.BufferData( target, size, data, BufferUsageHint.StaticDraw );

            return buffer;
        }

        private static int CreateGLVertexAttributeBuffer<T>( int size, T[] vertexData, int attributeIndex, int attributeSize ) where T : struct
        {
            // create buffer for vertex data store
            int buffer = CreateGLBuffer( BufferTarget.ArrayBuffer, size, vertexData );

            // configure vertex attribute
            GL.VertexAttribPointer( attributeIndex, attributeSize, VertexAttribPointerType.Float, false, 0, 0 );

            // enable vertex attribute
            GL.EnableVertexAttribArray( attributeIndex );

            return buffer;
        }

        private GLMaterial CreateGLMaterial( Material material )
        {
            var glMaterial = new GLMaterial();

            // color parameters
            glMaterial.Ambient = new Vector4( material.Ambient.X, material.Ambient.Y, material.Ambient.Z, material.Ambient.W );
            glMaterial.Diffuse = new Vector4( material.Diffuse.X, material.Diffuse.Y, material.Diffuse.Z, material.Diffuse.W );
            glMaterial.Specular = new Vector4( material.Specular.X, material.Specular.Y, material.Specular.Z, material.Specular.W );
            glMaterial.Emissive = new Vector4( material.Emissive.X, material.Emissive.Y, material.Emissive.Z, material.Emissive.W );

            // texture
            if ( material.DiffuseMap != null )
            {
                if ( mIsFieldModel && mFieldTextures.TryOpenFile( material.DiffuseMap.Name, out var textureStream ) )
                {
                    using ( textureStream )
                    {
                        var texture = new FieldTexture( textureStream );
                        glMaterial.DiffuseTextureId = CreateGLTexture( texture );
                    }
                }
                else
                {
                    var texture = mModel.TextureDictionary[material.DiffuseMap.Name];
                    glMaterial.DiffuseTextureId = CreateGLTexture( texture );
                }
            }

            return glMaterial;
        }

        //
        // Mouse events
        //

        protected override void OnMouseMove( System.Windows.Forms.MouseEventArgs e )
        {
            if ( !mIsModelLoaded )
                return;

            if ( e.Button.HasFlag( MouseButtons.Middle ) )
            {
                float multiplier = 1.0f;
                if ( Keyboard.GetState().IsKeyDown( Key.ShiftLeft ) )
                    multiplier = 8.0f;

                int xDelta = e.X - mLastMouseX;
                int yDelta = e.Y - mLastMouseY;

                if ( Keyboard.GetState().IsKeyDown( Key.AltLeft ) )
                {
                    // Orbit around model
                    var bSphere = mModel.Scene.BoundingSphere.Value;
                    mCamera = new GLPerspectiveTargetCamera( mCamera.Translation, mCamera.ZNear, mCamera.ZFar, mCamera.FieldOfView, mCamera.AspectRatio, new Vector3( bSphere.Center.X, bSphere.Center.Y, bSphere.Center.Z ) );
                    mCamera.Translation = new Vector3(
                        mCamera.Translation.X - ( ( xDelta / 3f ) * multiplier ),
                        mCamera.Translation.Y + ( ( yDelta / 3f ) * multiplier ),
                        mCamera.Translation.Z );
                }
                else
                {
                    // Move camera
                    mCamera = new GLPerspectiveFreeCamera( mCamera.Translation, mCamera.ZNear, mCamera.ZFar, mCamera.FieldOfView, mCamera.AspectRatio, Quaternion.Identity );
                    mCamera.Translation = new Vector3(
                        mCamera.Translation.X - ( ( xDelta / 3f ) * multiplier ),
                        mCamera.Translation.Y + ( ( yDelta / 3f ) * multiplier ),
                        mCamera.Translation.Z );
                }
            }

            mLastMouseX = e.X;
            mLastMouseY = e.Y;

            Invalidate();
        }

        protected override void OnMouseWheel( System.Windows.Forms.MouseEventArgs e )
        {
            if ( !mIsModelLoaded )
                return;

            float multiplier = 1.0f;
            if ( Keyboard.GetState().IsKeyDown( Key.ShiftLeft ) )
                multiplier = 8.0f;

            mCamera.Translation = Vector3.Subtract( mCamera.Translation, ( Vector3.UnitZ * ( ( (float)e.Delta * 8 ) * multiplier ) ) );

            Invalidate();
        }
    }

    public struct GLMaterial
    {
        public Vector4 Ambient;
        public Vector4 Diffuse;
        public Vector4 Specular;
        public Vector4 Emissive;
        public int DiffuseTextureId;

        public bool HasDiffuse => DiffuseTextureId != 0;
    }

    public struct GLGeometry
    {
        public int VertexArrayId;
        public int PositionBufferId;
        public int NormalBufferId;
        public int TextureCoordinateChannel0BufferId;
        public int ElementBufferId;
        public int ElementIndexCount;
        public GLMaterial Material;
        public Matrix4 ModelMatrix;
    }
}