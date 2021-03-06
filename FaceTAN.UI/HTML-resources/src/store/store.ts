import Vue from 'vue';
import Vuex from 'vuex';
import { GetterTree, MutationTree, ActionTree,  } from 'vuex';
import * as T from '../models/models';
import * as BindingObject from '../models/BindingObject';
import GlobalHelpers from '../GlobalHelpers';

let Global = new GlobalHelpers();

Vue.use(Vuex)

interface State {  
    
    dsmDataset: T.ImageList;
    dsmSubsets: T.ImageList[];

    tmTests: T.Test[];

    dsmView: T.DsmView;
}

const getters: GetterTree<State, any> = {
    
    getDataSet: (state, getters) => {
        return state.dsmDataset;
    },
    getSubsets: (state, getters) => {
        return state.dsmSubsets;
    },
    getSubset: (state, getters) => {
        return (searchSubsetGuid: string) => state.dsmSubsets.filter(subset => {
            return subset.guid == searchSubsetGuid;
        });
    },
    getTests: (state, getters) => {
        return state.tmTests;
    },
    getTest: (state, getters) => {
        return (searchTestGuid: string) => state.tmTests.filter(test => {
            return test.guid == searchTestGuid;      
        });
    },
    getView: (state, getters) => {
        return state.dsmView;
    }

}

const mutations: MutationTree<State> = {
    
    addDataSetImage: (state, payload: T.ImageElement) => {
        state.dsmDataset.imageStore.push(payload);
    },
    
    /* 
    * SUBSETS
    */
    reverse: (state) => state.dsmSubsets.reverse(),
     
    addSubset: (state, payload: T.ImageList) => {
        state.dsmSubsets.push(payload);
    },

    removeSubset:  (State, guid) => {
        state.dsmSubsets.forEach( (subset, index) => {
            if(subset.guid == guid){
                state.dsmSubsets.splice(index, 1);
            }
        });
    },

    /* 
    * TESTS
    */
    addTest: (state, payload: T.Test) => {
    state.tmTests.push(payload);
    },

    removeTest:  (State, guid) => {
        state.tmTests.forEach( (test, index) => {
            if(test.guid == guid){
                state.tmTests.splice(index, 1);
            }
        });
    },

    updateView: (state, payload: T.DsmView) => {
        state.dsmView = payload;
    }

}

declare var CefSharp: BindingObject.CefSharp;
declare var boundDataSet: BindingObject.DataSet;
declare var boundTestRunner: BindingObject.TestRunner;

const actions: ActionTree<State,any> = {

    fetchS3Images: async (state) => {

        await CefSharp.BindObjectAsync("boundDataSet", "boundDataSet");

        console.log("Sending");
        await boundDataSet.getImageArray().then(function (res: T.ImageElement[])
        {

            console.log("CEF Response: ");
            
            res.forEach( (index) => {
                console.log("Adding: " + index.name);  
                state.getters.getDataSet.imageStore.push(index);
            });
        });

    },

    requestTestResult: async (state, testObject: T.Test) => {
        await CefSharp.BindObjectAsync("boundTestRunner", "boundTestRunner");
        
        let sourceKeyArray: string[] = [];
        state.getters.getSubset(testObject.sourceGuid)[0].imageStore.forEach(element => {
            sourceKeyArray.push(element.name);
        });;

        let targetKeyArray: string[] = [];
        state.getters.getSubset(testObject.targetGuid)[0].imageStore.forEach((image: T.ImageElement) => {
            targetKeyArray.push(image.name);
        });;

        console.log("Sending Test Run Request...");
        console.log(sourceKeyArray);
        console.log(targetKeyArray);

        await boundTestRunner.runTest(testObject.api,  sourceKeyArray, targetKeyArray).then((res: string) => {
            console.log("CEF Response: ");
            console.log(res);
            testObject.result = res;
        });
    }
    
}

const state: State = {

    dsmDataset: {
        guid: Global.newGuid(),
        name: "Dataset",
        imageStore: [],
    },
    dsmSubsets: [],

    tmTests: [],

    dsmView: {
        edit: false,
        guid: undefined
    }
}

export default new Vuex.Store<State>({
    state,
    mutations,
    getters,
    actions
});
