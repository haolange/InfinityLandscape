﻿using UnityEngine;
using Unity.Mathematics;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;

namespace Landscape.RuntimeVirtualTexture
{
    public enum EFeedbackScale
    {
        X1 = 1,
        X2 = 2,
        X4 = 4,
        X8 = 8,
        X16 = 16
    }

    internal enum EVirtualTexturePass
    {
        VirtualTexture,
        DrawFeedback,
        DrawPageTable,
        DrawPageColor,
        CompressPage,
        CopyPageToPhyscis
    }

    internal class FeedbackRenderPass : ScriptableRenderPass
    {
        LayerMask m_LayerMask;
        ShaderTagId m_ShaderPassID;
        EFeedbackScale m_feedbackScale;
        FilteringSettings m_FilterSetting;
        ProfilingSampler m_DrawFeedbackSampler;
        ProfilingSampler m_DrawPageTableSampler;
        ProfilingSampler m_DrawPageColorSampler;
        ProfilingSampler m_VirtualTextureSampler;
        RenderTexture m_FeedbackTexture;
        RenderTargetIdentifier m_FeedbackTextureID;
        FVirtualTextureFeedback m_FeedbackProcessor;

        public FeedbackRenderPass(in LayerMask layerMask, in EFeedbackScale feedbackScale)
        {
            m_LayerMask = layerMask;
            m_feedbackScale = feedbackScale;
            m_ShaderPassID = new ShaderTagId("VTFeedback");
            m_FilterSetting = new FilteringSettings(RenderQueueRange.opaque, m_LayerMask);
            m_DrawFeedbackSampler = ProfilingSampler.Get(EVirtualTexturePass.DrawFeedback);
            m_DrawPageTableSampler = ProfilingSampler.Get(EVirtualTexturePass.DrawPageTable);
            m_DrawPageColorSampler = ProfilingSampler.Get(EVirtualTexturePass.DrawPageColor);
            m_VirtualTextureSampler = ProfilingSampler.Get(EVirtualTexturePass.VirtualTexture);
            m_FeedbackProcessor = new FVirtualTextureFeedback(true);
        }

        public override void OnCameraSetup(CommandBuffer cmdBuffer, ref RenderingData renderingData)
        {
            Camera camera = renderingData.cameraData.camera;
            GraphicsFormat format = GraphicsFormat.R8G8B8A8_UNorm;
            /*switch (FVirtualTextureFeedback.bits)
            {
                case FeedbackBits.B16:
                    format = GraphicsFormat.R16G16B16A16_SFloat;
                    break;
            }*/

            int2 feedSize = new int2(math.min(1920, camera.pixelWidth), math.min(1080, camera.pixelHeight));
            m_FeedbackTexture = RenderTexture.GetTemporary(feedSize.x / (int)m_feedbackScale, feedSize.y / (int)m_feedbackScale, 1, format, 1);
            m_FeedbackTexture.name = "FeedbackTexture";
            m_FeedbackTextureID = new RenderTargetIdentifier(m_FeedbackTexture);
            //ConfigureTarget(m_FeedbackTextureID);
            //ConfigureClear(ClearFlag.All, Color.black);
        }

        public override void Execute(ScriptableRenderContext renderContext, ref RenderingData renderingData)
        {
            if (!Application.isPlaying || VirtualTextureVolume.s_VirtualTextureVolume == null) { return; }

            CommandBuffer cmdBuffer = CommandBufferPool.Get();
            Camera camera = renderingData.cameraData.camera;

            using (new ProfilingScope(cmdBuffer, m_VirtualTextureSampler))
            {
                renderContext.ExecuteCommandBuffer(cmdBuffer);
                cmdBuffer.Clear();
                DrawVirtualTexture(renderContext, cmdBuffer, camera, ref renderingData);
            }

            renderContext.ExecuteCommandBuffer(cmdBuffer);
            CommandBufferPool.Release(cmdBuffer);
        }

        public override void OnCameraCleanup(CommandBuffer cmdBuffer)
        {
            RenderTexture.ReleaseTemporary(m_FeedbackTexture);
        }

        public unsafe void DrawVirtualTexture(ScriptableRenderContext renderContext, CommandBuffer cmdBuffer, Camera camera, ref RenderingData renderingData)
        {
            FPageProducer pageProducer = VirtualTextureVolume.s_VirtualTextureVolume.pageProducer;
            FPageRenderer pageRenderer = VirtualTextureVolume.s_VirtualTextureVolume.pageRenderer;
            VirtualTextureAsset virtualTexture = VirtualTextureVolume.s_VirtualTextureVolume.virtualTexture;

            if (m_FeedbackProcessor.isReady)
            {
                using (new ProfilingScope(cmdBuffer, m_DrawPageTableSampler))
                {
                    if (m_FeedbackProcessor.readbackDatas.IsCreated)
                    {
                        pageProducer.ProcessFeedback(ref m_FeedbackProcessor.readbackDatas, virtualTexture.NumMip, virtualTexture.tileNum, virtualTexture.pageSize, virtualTexture.lruCache, ref pageRenderer.loadRequests);
                        
                        cmdBuffer.SetRenderTarget(virtualTexture.tableTextureID);
                        pageRenderer.DrawPageTable(renderContext, cmdBuffer, pageProducer);
                    }
                }

                using (new ProfilingScope(cmdBuffer, m_DrawFeedbackSampler))
                {
                    DrawingSettings drawSetting = new DrawingSettings(m_ShaderPassID, new SortingSettings(camera) { criteria = SortingCriteria.QuantizedFrontToBack })
                    {
                        enableInstancing = true,
                    };
                    cmdBuffer.SetRenderTarget(m_FeedbackTextureID);
                    cmdBuffer.ClearRenderTarget(true, true, Color.black);
                    // x: 页表大小(单位: 页)
                    // y: 虚拟贴图大小(单位: 像素)
                    // z: 最大mipmap等级
                    // w: mipBias
                    cmdBuffer.SetGlobalVector("_VTFeedbackParams", new Vector4(virtualTexture.pageSize, virtualTexture.pageSize * virtualTexture.tileSize * (1.0f / (float)m_feedbackScale), virtualTexture.NumMip, 0.1f));
                    
                    float cameraAspect = (float) camera.pixelRect.width / (float) camera.pixelRect.height;
                    Matrix4x4 projectionMatrix = Matrix4x4.Perspective(80, cameraAspect, camera.nearClipPlane, camera.farClipPlane);
                    projectionMatrix = GL.GetGPUProjectionMatrix(projectionMatrix, true);
                    RenderingUtils.SetViewAndProjectionMatrices(cmdBuffer, camera.worldToCameraMatrix, projectionMatrix, false);
                    renderContext.ExecuteCommandBuffer(cmdBuffer);
                    cmdBuffer.Clear();

                    renderContext.DrawRenderers(renderingData.cullResults, ref drawSetting, ref m_FilterSetting);

                    projectionMatrix = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
                    RenderingUtils.SetViewAndProjectionMatrices(cmdBuffer, camera.worldToCameraMatrix, projectionMatrix, false);
                    renderContext.ExecuteCommandBuffer(cmdBuffer);
                    cmdBuffer.Clear();
                }

                //read-back feedback
                m_FeedbackProcessor.RequestReadback(cmdBuffer, m_FeedbackTexture);
            }

            using (new ProfilingScope(cmdBuffer, m_DrawPageColorSampler))
            {
                cmdBuffer.SetRenderTarget(virtualTexture.colorTextureIDs, virtualTexture.colorTextureIDs[0]);
                renderContext.ExecuteCommandBuffer(cmdBuffer);
                cmdBuffer.Clear();

                FDrawPageParameter drawPageParameter = VirtualTextureVolume.s_VirtualTextureVolume.GetDrawPageParamter();
                pageRenderer.DrawPageColor(renderContext, cmdBuffer, pageProducer, virtualTexture, ref virtualTexture.lruCache[0], drawPageParameter);
            }
        }
    }

    public class FeedbackRenderer : ScriptableRendererFeature
    {
        public LayerMask layerMask;
        public EFeedbackScale feedbackScale;

        FeedbackRenderPass m_FeedbackRenderPass;

        public override void Create()
        {
            m_FeedbackRenderPass = new FeedbackRenderPass(layerMask, feedbackScale);
            m_FeedbackRenderPass.renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(m_FeedbackRenderPass);
        }

        protected override void Dispose(bool disposing)
        {

        }
    }
}
