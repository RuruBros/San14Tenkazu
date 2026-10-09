"""Constant-buffer metadata from local D3DReflect; never attaches to SAN14.

Child reflection objects are borrowed from the parent, not IUnknown interfaces.
Offsets/struct fields follow Windows SDK d3d11shader.h for x64.
"""
import ctypes as c
from season_shader_match import Reflector


class BufferDesc(c.Structure):
    _fields_=[('Name',c.c_void_p),('Type',c.c_uint32),('Variables',c.c_uint32),
              ('Size',c.c_uint32),('Flags',c.c_uint32)]


class VariableDesc(c.Structure):
    _fields_=[('Name',c.c_void_p),('StartOffset',c.c_uint32),('Size',c.c_uint32),('Flags',c.c_uint32),
              ('DefaultValue',c.c_void_p),('StartTexture',c.c_uint32),('TextureSize',c.c_uint32),
              ('StartSampler',c.c_uint32),('SamplerSize',c.c_uint32)]


class TypeDesc(c.Structure):
    _fields_=[('Class',c.c_uint32),('Type',c.c_uint32),('Rows',c.c_uint32),('Columns',c.c_uint32),
              ('Elements',c.c_uint32),('Members',c.c_uint32),('Offset',c.c_uint32),('Name',c.c_void_p)]


def validate_variable(buffer_size,offset,size):
    if not 0<=buffer_size<=65536 or not 0<=offset<=buffer_size or not 0<=size<=buffer_size-offset:
        raise ValueError('Local reflected variable exceeds constant-buffer bounds')


def buffer_binding(layout,resources):
    # D3D_CT_RESOURCE_BIND_INFO describes a resource's element, not a GPU
    # constant buffer. GetConstantBufferByIndex can return this descriptor too.
    allowed={0:{0},1:{1},3:{2,4,5,6,7,8,9,10,11}}
    kind=layout['bufferType']
    if kind not in allowed:raise ValueError('Unsupported local reflection buffer kind')
    matches=[r for r in resources if r['name']==layout['name'] and r['kind'] in allowed[kind]]
    if len(matches)==1:return matches[0]
    if not matches and not any(v['flags']&2 for v in layout['variables']):return None
    raise ValueError('Used reflected buffer binding is ambiguous or absent: '+layout['name'])


class LayoutReflector(Reflector):
    def type_layout(self,pointer,budget,active=None,depth=0):
        active=set() if active is None else active
        if not pointer or pointer in active or depth>8 or budget[0]>=8192:
            raise ValueError('Local reflected type recursion/count rejected')
        budget[0]+=1;active.add(pointer)
        try:
            detail=TypeDesc()
            if self.method(pointer,0,(c.c_void_p,))(pointer,c.byref(detail))<0:
                raise ValueError('Local type GetDesc failed')
            if detail.Members>256:raise ValueError('Local type member count rejected')
            members=[]
            for index in range(detail.Members):
                child=self.method(pointer,1,(c.c_uint32,),c.c_void_p)(pointer,index)
                name=self.method(pointer,3,(c.c_uint32,),c.c_void_p)(pointer,index)
                if not name:raise ValueError('Local type member name is absent')
                members.append(dict(memberName=c.string_at(name).decode('ascii'),
                                    **self.type_layout(child,budget,active,depth+1)))
            return dict(variableClass=detail.Class,variableType=detail.Type,rows=detail.Rows,columns=detail.Columns,
                        elements=detail.Elements,members=detail.Members,offset=detail.Offset,
                        name=c.string_at(detail.Name).decode('ascii') if detail.Name else None,memberLayouts=members)
        finally:active.remove(pointer)

    def layouts(self,data):
        if c.sizeof(BufferDesc)!=24 or c.sizeof(VariableDesc)!=48 or c.sizeof(TypeDesc)!=40:
            raise ValueError('Reflection layout requires the inspected x64 ABI')
        storage=c.create_string_buffer(data);obj=c.c_void_p()
        status=self.reflect(storage,len(data),self.iid,c.byref(obj))
        if status<0 or not obj:raise ValueError('Local D3DReflect failed')
        try:
            raw=c.create_string_buffer(160)
            if self.method(obj,3,(c.c_void_p,))(obj,raw)<0:raise ValueError('Local shader GetDesc failed')
            count=c.c_uint32.from_buffer(raw,20).value
            if count>32:raise ValueError('Local constant-buffer count rejected')
            result=[];type_budget=[0]
            for index in range(count):
                child=self.method(obj,4,(c.c_uint32,),c.c_void_p)(obj,index)
                if not child:raise ValueError('Local constant-buffer reflection is absent')
                desc=BufferDesc()
                if self.method(child,0,(c.c_void_p,))(child,c.byref(desc))<0:
                    raise ValueError('Local constant-buffer GetDesc failed')
                if desc.Variables>2048 or desc.Size>65536 or not desc.Name:
                    raise ValueError('Local constant-buffer shape rejected')
                variables=[]
                for position in range(desc.Variables):
                    var=self.method(child,1,(c.c_uint32,),c.c_void_p)(child,position)
                    if not var:raise ValueError('Local variable reflection is absent')
                    value=VariableDesc()
                    if self.method(var,0,(c.c_void_p,))(var,c.byref(value))<0:
                        raise ValueError('Local variable GetDesc failed')
                    validate_variable(desc.Size,value.StartOffset,value.Size)
                    if not value.Name:raise ValueError('Local variable name is absent')
                    kind=self.method(var,1,(),c.c_void_p)(var)
                    if not kind:raise ValueError('Local variable type is absent')
                    variables.append(dict(name=c.string_at(value.Name).decode('ascii'),offset=value.StartOffset,
                        size=value.Size,flags=value.Flags,defaultHex=c.string_at(value.DefaultValue,value.Size).hex() if value.DefaultValue else None,
                        startTexture=value.StartTexture,textureSize=value.TextureSize,startSampler=value.StartSampler,samplerSize=value.SamplerSize,
                        type=self.type_layout(kind,type_budget)))
                result.append(dict(index=index,name=c.string_at(desc.Name).decode('ascii'),bufferType=desc.Type,
                                   size=desc.Size,flags=desc.Flags,variables=variables))
            return result
        finally:self.method(obj,2)(obj)
