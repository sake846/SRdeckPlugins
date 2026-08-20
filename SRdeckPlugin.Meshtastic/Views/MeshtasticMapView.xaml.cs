using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SRdeckPlugin.Meshtastic.ViewModels;
using SRdeckPlugin.Wpf;

// View exported by the Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Views;

public partial class MeshtasticMapView : UserControl
{
    private MeshtasticViewModel? _viewModel;
    private bool _isMapReady;
    private bool _isInitializing;
    private readonly DispatcherTimer _markerUpdateTimer;

    public MeshtasticMapView()
    {
        InitializeComponent();
        ApplyMapBackground();
        _markerUpdateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _markerUpdateTimer.Tick += async (_, _) =>
        {
            _markerUpdateTimer.Stop();
            await UpdateMarkersAsync();
        };
        DataContextChanged += OnDataContextChanged;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(DataContext as MeshtasticViewModel);
        if (_isMapReady || _isInitializing) return;

        _isInitializing = true;
        try
        {
            await _mapWebView.EnsureCoreWebView2Async();
            GeoMapWebViewSecurity.Configure(_mapWebView.CoreWebView2);
            _mapWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            _mapWebView.NavigationCompleted += MapWebView_NavigationCompleted;
            _mapWebView.NavigateToString(BuildMapHtml());
        }
        catch (Exception exception)
        {
            _statusText.Text = $"埋め込み地図を開始できません。\nWebView2 Runtimeを確認してください。\n{exception.Message}";
            _statusOverlay.Visibility = Visibility.Visible;
            _isInitializing = false;
        }
    }

    private void UserControl_Unloaded(object sender, RoutedEventArgs e)
    {
        _markerUpdateTimer.Stop();
        DetachViewModel();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel();
        if (IsLoaded) AttachViewModel(e.NewValue as MeshtasticViewModel);
    }

    private void AttachViewModel(MeshtasticViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel)) return;
        DetachViewModel();
        _viewModel = viewModel;
        if (_viewModel is null) return;
        _viewModel.VisibleMeshtasticMapPoints.CollectionChanged += MapPoints_CollectionChanged;
        foreach (MeshtasticMapPoint point in _viewModel.VisibleMeshtasticMapPoints)
            point.PropertyChanged += MapPoint_PropertyChanged;
        ScheduleMarkerUpdate();
    }

    private void DetachViewModel()
    {
        if (_viewModel is null) return;
        _viewModel.VisibleMeshtasticMapPoints.CollectionChanged -= MapPoints_CollectionChanged;
        foreach (MeshtasticMapPoint point in _viewModel.VisibleMeshtasticMapPoints)
            point.PropertyChanged -= MapPoint_PropertyChanged;
        _viewModel = null;
    }

    private void MapPoints_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (MeshtasticMapPoint point in e.OldItems) point.PropertyChanged -= MapPoint_PropertyChanged;
        if (e.NewItems is not null)
            foreach (MeshtasticMapPoint point in e.NewItems) point.PropertyChanged += MapPoint_PropertyChanged;
        ScheduleMarkerUpdate();
    }

    private void MapPoint_PropertyChanged(object? sender, PropertyChangedEventArgs e) => ScheduleMarkerUpdate();

    private void ScheduleMarkerUpdate()
    {
        if (!_markerUpdateTimer.IsEnabled) _markerUpdateTimer.Start();
    }

    private void ApplyMapBackground()
    {
        System.Windows.Media.Color color = GetThemeColor("PanelBaseBrush", System.Windows.Media.Color.FromRgb(18, 18, 18));
        _mapWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    private string BuildMapHtml()
    {
        SRdeckPlugin.Wpf.GeoMapState state = SRdeckPlugin.Wpf.GeoMapStateStore.GetState("meshtastic");
        string latStr = state.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string lngStr = state.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string zoomStr = state.Zoom.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string mapStatusCss = $"#map-status{{position:absolute;inset:0;z-index:2000;display:flex;align-items:center;justify-content:center;box-sizing:border-box;padding:18px;background:{GetThemeCss("PanelBaseBrush", 18, 18, 18)};color:{GetThemeCss("TextDimBrush", 184, 184, 184)};font:12px sans-serif;text-align:center;pointer-events:none}}#map-status.hidden{{display:none}}";

        return MapHtml
            .Replace("__INIT_LAT__", latStr, StringComparison.Ordinal)
            .Replace("__INIT_LNG__", lngStr, StringComparison.Ordinal)
            .Replace("__INIT_ZOOM__", zoomStr, StringComparison.Ordinal)
            .Replace("__PANEL_BACKGROUND__", GetThemeCss("PanelBaseBrush", 18, 18, 18), StringComparison.Ordinal)
            .Replace("__PANEL_BASE__", GetThemeCss("PanelBaseBrush", 18, 18, 18), StringComparison.Ordinal)
            .Replace("__PANEL_SURFACE__", GetThemeCss("PanelSurfaceBrush", 30, 30, 30), StringComparison.Ordinal)
            .Replace("__CONTROL_BORDER__", GetThemeCss("ControlBorderBrush", 136, 136, 136), StringComparison.Ordinal)
            .Replace("__TEXT_PRIMARY__", GetThemeCss("TextPrimaryBrush", 242, 242, 242), StringComparison.Ordinal)
            .Replace("__TEXT_SECONDARY__", GetThemeCss("TextSecondaryBrush", 214, 214, 214), StringComparison.Ordinal)
            .Replace("__TEXT_DIM__", GetThemeCss("TextDimBrush", 184, 184, 184), StringComparison.Ordinal)
            .Replace("__FOCUS__", GetThemeCss("PluginFocusBorderBrush", 0, 253, 255), StringComparison.Ordinal)
            .Replace("__SERIES_1__", GetThemeCss("PluginDataSeries1Brush", 77, 208, 225), StringComparison.Ordinal)
            .Replace("__SERIES_3__", GetThemeCss("PluginDataSeries3Brush", 255, 183, 77), StringComparison.Ordinal)
            .Replace("__SERIES_4__", GetThemeCss("PluginDataSeries4Brush", 38, 166, 154), StringComparison.Ordinal)
            .Replace("__OVERLAY_86__", GetThemeCssRgba("PanelBaseBrush", 18, 18, 18, 0.86), StringComparison.Ordinal)
            .Replace("<body>", $"<body><style>{mapStatusCss}</style>", StringComparison.Ordinal);
    }

    private string GetThemeCss(string resourceKey, byte fallbackRed, byte fallbackGreen, byte fallbackBlue)
    {
        System.Windows.Media.Color color = GetThemeColor(
            resourceKey, System.Windows.Media.Color.FromRgb(fallbackRed, fallbackGreen, fallbackBlue));
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private string GetThemeCssRgba(string resourceKey, byte fallbackRed, byte fallbackGreen, byte fallbackBlue, double opacity)
    {
        System.Windows.Media.Color color = GetThemeColor(
            resourceKey, System.Windows.Media.Color.FromRgb(fallbackRed, fallbackGreen, fallbackBlue));
        return FormattableString.Invariant($"rgba({color.R},{color.G},{color.B},{opacity:0.##})");
    }

    private System.Windows.Media.Color GetThemeColor(string resourceKey, System.Windows.Media.Color fallback) =>
        (TryFindResource(resourceKey) as SolidColorBrush)?.Color ?? fallback;

    private void CoreWebView2_WebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_viewModel is null) return;
        string value;
        try { value = e.TryGetWebMessageAsString(); }
        catch (ArgumentException) { return; }
        if (string.IsNullOrEmpty(value)) return;
        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("type", out var typeProp) &&
                typeProp.GetString() == "mapState")
            {
                double lat = doc.RootElement.GetProperty("lat").GetDouble();
                double lng = doc.RootElement.GetProperty("lng").GetDouble();
                double zoom = doc.RootElement.GetProperty("zoom").GetDouble();
                SRdeckPlugin.Wpf.GeoMapStateStore.SaveState("meshtastic", new SRdeckPlugin.Wpf.GeoMapState(lat, lng, zoom));
                return;
            }
        }
        catch { }

        if (uint.TryParse(value, out uint nodeNumber)) _viewModel.SelectMeshtasticNode(nodeNumber);
    }

    private async void MapWebView_NavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        _isInitializing = false;
        if (!e.IsSuccess)
        {
            _statusText.Text = "地図ページを読み込めませんでした。ネットワーク接続を確認してください。";
            await SetMapStatusAsync(_statusText.Text);
            return;
        }
        string leafletReady = await _mapWebView.ExecuteScriptAsync(
            "typeof L !== 'undefined' && typeof window.updateMeshtasticMarkers === 'function'");
        if (!string.Equals(leafletReady, "true", StringComparison.OrdinalIgnoreCase))
        {
            _statusText.Text = "地図ライブラリを読み込めませんでした。ネットワーク接続を確認してください。";
            await SetMapStatusAsync(_statusText.Text);
            _statusOverlay.Visibility = Visibility.Visible;
            return;
        }
        _isMapReady = true;
        _statusOverlay.Visibility = Visibility.Collapsed;
        await InvalidateMapSizeAsync();
        await UpdateMarkersAsync();
    }

    private void OnMapSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0) _ = InvalidateMapSizeAsync();
    }

    private async Task InvalidateMapSizeAsync()
    {
        if (!_isMapReady || _mapWebView.CoreWebView2 is null) return;
        try { await _mapWebView.ExecuteScriptAsync("window.invalidateMapSize && window.invalidateMapSize();"); }
        catch (InvalidOperationException) { }
    }

    private async Task SetMapStatusAsync(string message)
    {
        if (_mapWebView.CoreWebView2 is null) return;
        try
        {
            await _mapWebView.ExecuteScriptAsync(
                $"window.setMapStatus && window.setMapStatus({JsonSerializer.Serialize(message)});");
        }
        catch (InvalidOperationException) { }
    }

    private async Task UpdateMarkersAsync()
    {
        if (!_isMapReady || _viewModel is null || _mapWebView.CoreWebView2 is null) return;
        var markers = _viewModel.VisibleMeshtasticMapPoints.Select(point => new
        {
            nodeNumber = point.NodeNumber,
            latitude = point.Latitude,
            longitude = point.Longitude,
            label = point.Label,
            coordinates = point.Coordinates,
            activityStatus = point.ActivityStatus,
            selected = point.IsSelected,
            direct = point.HasDirectReception
        }).ToArray();
        string json = JsonSerializer.Serialize(markers);
        try { await _mapWebView.ExecuteScriptAsync($"window.updateMeshtasticMarkers({json})"); }
        catch (InvalidOperationException) { }
    }

    private const string MapHtml = """
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline' https://unpkg.com; script-src 'unsafe-inline' https://unpkg.com; img-src data: https://unpkg.com https://tile.openstreetmap.org; connect-src https://tile.openstreetmap.org">
<link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css" integrity="sha256-p4NxAoJBhIIN+hmNHrzRCf9tD/miZyoHS5obTRR9BMY=" crossorigin="">
<style>
html,body,#map{height:100%;margin:0;background:__PANEL_BACKGROUND__}
.leaflet-container{font:12px sans-serif;line-height:1.35}
.leaflet-control-attribution{font-size:12px;line-height:16px;padding:2px 4px}
.mesh-marker{width:14px;height:14px;border-radius:50%;border:2px solid __PANEL_SURFACE__;box-shadow:0 0 4px __PANEL_BASE__}
.mesh-marker.selected{border:3px solid __FOCUS__;box-shadow:0 0 8px __FOCUS__}
.leaflet-tooltip.mesh-callout:before{display:none !important}
.leaflet-tooltip.mesh-callout{margin:0 !important;background:__PANEL_SURFACE__;border:1.5px solid __CONTROL_BORDER__;border-radius:5px;box-shadow:0 2px 6px rgba(0,0,0,0.5);color:__TEXT_PRIMARY__;font:600 11px sans-serif;padding:3px 7px;white-space:nowrap;cursor:pointer !important;pointer-events:auto !important;opacity:0.95}
.leaflet-tooltip.mesh-callout *{cursor:pointer !important}
.leaflet-tooltip.mesh-callout.selected{border-color:__FOCUS__;box-shadow:0 0 8px __FOCUS__}
.mesh-callout-badge{display:inline-block;width:8px;height:8px;border-radius:50%;margin-right:5px;vertical-align:middle}
.map-legend{background:__OVERLAY_86__;color:__TEXT_SECONDARY__;padding:6px 8px;border:1px solid __CONTROL_BORDER__;border-radius:3px;line-height:18px}.map-legend i{display:inline-block;width:9px;height:9px;margin-right:5px;border-radius:50%}
</style>
</head><body><div id="map"></div><div id="map-status">地図を読み込んでいます…</div><script>window.setMapStatus=function(message){const status=document.getElementById('map-status');if(!status)return;status.textContent=message;status.classList.toggle('hidden',!message)};window.invalidateMapSize=function(){};window.addEventListener('error',()=>window.setMapStatus('地図スクリプトでエラーが発生しました。ネットワーク接続を確認してください。'));window.addEventListener('unhandledrejection',()=>window.setMapStatus('地図スクリプトでエラーが発生しました。ネットワーク接続を確認してください。'));setTimeout(()=>{if(window.L===undefined)window.setMapStatus('地図ライブラリを読み込めませんでした。ネットワーク接続を確認してください。')},8000);</script><script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js" integrity="sha256-20nQCchB9co0qIjJZRGuk2/Z9VM+kNiyxNV1lvTlZBo=" crossorigin=""></script><script>
const map=L.map('map',{zoomControl:true}).setView([__INIT_LAT__,__INIT_LNG__],__INIT_ZOOM__);
let tileErrors=0;const tiles=L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png',{maxZoom:19,attribution:'&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener noreferrer">OpenStreetMap</a> contributors (ODbL 1.0)'}).on('tileerror',()=>{tileErrors++;window.setMapStatus('地図タイルを読み込めません。ネットワーク接続を確認してください。')}).on('tileload',()=>{if(tileErrors===0)window.setMapStatus('')}).addTo(map);window.invalidateMapSize=()=>map.invalidateSize({pan:false});
let markerLayer=L.layerGroup().addTo(map);
let leaderLayer=L.layerGroup().addTo(map);
const markerMap=new Map();
const legend=L.control({position:'bottomright'});
legend.onAdd=()=>{const d=L.DomUtil.create('div','map-legend');d.innerHTML='<i style="background:__SERIES_3__"></i>新規 <i style="background:__SERIES_1__"></i>活動中 <i style="background:__SERIES_4__"></i>最近 <i style="background:__TEXT_DIM__"></i>休止';return d};

function selectNode(nodeNum){
 if(window.chrome&&window.chrome.webview){
  window.chrome.webview.postMessage(String(nodeNum));
 }
}

function postMapState(){
 const c=map.getCenter();
 const z=map.getZoom();
 if(window.chrome&&window.chrome.webview){
  window.chrome.webview.postMessage(JSON.stringify({type:'mapState',lat:c.lat,lng:c.lng,zoom:z}));
 }
}

const angledSlots=[
 {dir:'right',dx:60,dy:-25},
 {dir:'left',dx:-60,dy:-25},
 {dir:'right',dx:60,dy:25},
 {dir:'left',dx:-60,dy:25},
 {dir:'right',dx:90,dy:-55},
 {dir:'left',dx:-90,dy:-55},
 {dir:'right',dx:90,dy:55},
 {dir:'left',dx:-90,dy:55}
];

function layoutCallouts(){
 leaderLayer.clearLayers();
 if(markerMap.size===0) return;
 const entries=Array.from(markerMap.values());
 const items=entries.map(m=>({
  data:m,
  pt:map.latLngToLayerPoint(m.marker.getLatLng())
 }));
 const CLUSTER_DIST_SQ=140*140;
 const clusters=[];
 for(let i=0;i<items.length;i++){
  let assignedCluster=null;
  for(let c=0;c<clusters.length;c++){
   for(let j=0;j<clusters[c].length;j++){
    const dx=items[i].pt.x-clusters[c][j].pt.x;
    const dy=items[i].pt.y-clusters[c][j].pt.y;
    if(dx*dx+dy*dy<CLUSTER_DIST_SQ){
     assignedCluster=clusters[c];
     break;
    }
   }
   if(assignedCluster) break;
  }
  if(assignedCluster){
   assignedCluster.push(items[i]);
  }else{
   clusters.push([items[i]]);
  }
 }
 clusters.forEach(cluster=>{
  if(cluster.length===1){
   const m=cluster[0].data;
   bindCallout(m,'top',[0,-12]);
   drawLeaderLine(m.marker.getLatLng(),cluster[0].pt,0,-12,m.point.selected);
   return;
  }
  cluster.sort((a,b)=>a.pt.y-b.pt.y);
  cluster.forEach((item,idx)=>{
   const m=item.data;
   const slot=angledSlots[idx%angledSlots.length];
   const tier=Math.floor(idx/angledSlots.length);
   const extraX=tier*30*(slot.dx>0?1:-1);
   const extraY=tier*30*(slot.dy>0?1:-1);
   const offsetX=slot.dx+extraX;
   const offsetY=slot.dy+extraY;

   bindCallout(m,slot.dir,[offsetX,offsetY]);
   drawLeaderLine(m.marker.getLatLng(),item.pt,offsetX,offsetY,m.point.selected);
  });
 });
}

function drawLeaderLine(markerLL,markerPt,offsetX,offsetY,isSelected){
 if(Math.abs(offsetX)<6&&Math.abs(offsetY)<6) return;
 const tx=markerPt.x+offsetX;
 const ty=markerPt.y+offsetY;
 const dx=tx-markerPt.x;
 const dy=ty-markerPt.y;
 const dist=Math.sqrt(dx*dx+dy*dy);
 if(dist===0) return;
 const extX=tx+(dx/dist)*4;
 const extY=ty+(dy/dist)*4;
 const endLL=map.layerPointToLatLng(L.point(extX,extY));
 const outerColor=isSelected?'__FOCUS__':'__CONTROL_BORDER__';

 L.polyline([markerLL,endLL],{
  color:outerColor,
  weight:4,
  opacity:0.95,
  interactive:false
 }).addTo(leaderLayer);

 L.polyline([markerLL,endLL],{
  color:'__PANEL_SURFACE__',
  weight:2,
  opacity:1.0,
  interactive:false
 }).addTo(leaderLayer);
}

function bindCallout(m,dir,offset){
 const isSel=m.point.selected;
 const tooltipClass='mesh-callout'+(isSel?' selected':'');
 const existingTooltip=m.marker.getTooltip();
 if(m.currentDir===dir&&m.currentOffset&&m.currentOffset[0]===offset[0]&&m.currentOffset[1]===offset[1]&&existingTooltip){
  const el=existingTooltip.getElement();
  if(el){
   el.className='leaflet-tooltip leaflet-zoom-animated leaflet-tooltip-'+dir+' '+tooltipClass;
   attachTooltipEvents(el,m);
  }
  return;
 }
 m.currentDir=dir;
 m.currentOffset=offset;
 if(existingTooltip){
  m.marker.unbindTooltip();
 }
 const tooltip=L.tooltip({
  permanent:true,
  direction:dir,
  offset:offset,
  className:tooltipClass,
  interactive:true
 }).setContent(m.htmlContent);
 m.marker.bindTooltip(tooltip);
 const t=m.marker.getTooltip();
 if(t){
  setTimeout(()=>{
   const el=t.getElement();
   if(el){
    attachTooltipEvents(el,m);
   }
  },0);
 }
}

function attachTooltipEvents(el,m){
 if(el._hasMeshEvents) return;
 el._hasMeshEvents=true;
 L.DomEvent.disableClickPropagation(el);
 L.DomEvent.disableScrollPropagation(el);
 L.DomEvent.on(el,'click',function(e){
  if(e){
   L.DomEvent.stopPropagation(e);
  }
  selectNode(m.point.nodeNumber);
  setTimeout(()=>{
   m.marker.openPopup();
  },10);
 });
}

map.on('zoomend moveend viewreset',layoutCallouts);
map.on('moveend zoomend',postMapState);

window.updateMeshtasticMarkers=function(points){
 if(!points||points.length===0){
   markerMap.forEach(m=>markerLayer.removeLayer(m.marker));
   markerMap.clear();
   if(legend._map) legend.remove();
   return;
  }
  if(!legend._map) legend.addTo(map);
 const incomingIds=new Set(points.map(p=>p.nodeNumber));
 markerMap.forEach((m,nodeNum)=>{
  if(!incomingIds.has(nodeNum)){
   markerLayer.removeLayer(m.marker);
   markerMap.delete(nodeNum);
  }
 });

 const bounds=[];
 points.forEach(p=>{
  const ll=[p.latitude,p.longitude];
  bounds.push(ll);
  const color=p.activityStatus==='新規'?'__SERIES_3__':p.activityStatus==='活動中'?'__SERIES_1__':p.activityStatus==='最近'?'__SERIES_4__':'__TEXT_DIM__';

  if(markerMap.has(p.nodeNumber)){
   const m=markerMap.get(p.nodeNumber);
   m.point=p;
   m.marker.setLatLng(ll);
   m.marker.setZIndexOffset(p.selected?1000:0);
   const iconEl=m.marker.getElement();
   if(iconEl){
    const dot=iconEl.querySelector('.mesh-marker');
    if(dot){
     dot.className='mesh-marker'+(p.selected?' selected':'');
     dot.style.background=color;
    }
   }
   m.htmlContent='<span class="mesh-callout-badge" style="background:'+color+'"></span>'+escapeHtml(p.label);
   if(m.marker.getTooltip()){
    m.marker.getTooltip().setContent(m.htmlContent);
   }
  }else{
   const icon=L.divIcon({
    className:'',
    html:'<div class="mesh-marker'+(p.selected?' selected':'')+'" style="background:'+color+'"></div>',
    iconSize:[18,18],
    iconAnchor:[9,9]
   });
   const marker=L.marker(ll,{icon,zIndexOffset:p.selected?1000:0}).addTo(markerLayer);
   marker.bindPopup('<b>'+escapeHtml(p.label)+'</b><br>'+escapeHtml(p.activityStatus)+(p.direct?' / 直接受信あり':'')+'<br>'+escapeHtml(p.coordinates));
   marker.on('click',(e)=>{
    if(e&&e.originalEvent){
     L.DomEvent.stopPropagation(e.originalEvent);
    }
    selectNode(p.nodeNumber);
    setTimeout(()=>{
     marker.openPopup();
    },10);
   });
   const htmlContent='<span class="mesh-callout-badge" style="background:'+color+'"></span>'+escapeHtml(p.label);
   markerMap.set(p.nodeNumber,{point:p,marker:marker,htmlContent:htmlContent,currentDir:null,currentOffset:null});
  }
 });

 layoutCallouts();
};

function escapeHtml(v){const d=document.createElement('div');d.textContent=v||'';return d.innerHTML;}
</script></body></html>
""";
}
